using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SaidNest.Capture;

sealed class CaptureTrack(TrackInfo info, WasapiRecorder recorder, MMDevice? device) : IDisposable
{
    public TrackInfo Info { get; } = info;
    public WasapiRecorder Recorder { get; } = recorder;
    public long Bytes;
    public long Discontinuities;
    public long TimestampErrors;
    public float Peak;
    public long LastCallback;
    public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose() { Recorder.Dispose(); device?.Dispose(); }
}

static class Recorder
{
    public static async Task<object[]> OutputActivity()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var results = new List<object>();
        foreach (var device in devices)
        {
            using (device)
            {
                float peak = 0;
                for (int sample = 0; sample < 10; sample++) { peak = Math.Max(peak, device.AudioMeterInformation.MasterPeakValue); await Task.Delay(50); }
                int? discord = null;
                try { discord=EndpointSessions.DiscordCount(device.ID); }
                catch (System.Runtime.InteropServices.COMException) { }
                results.Add(new { device.ID, name=device.FriendlyName, peakDbfs=20*Math.Log10(Math.Max(peak,1e-10)), discordSessions=discord });
            }
        }
        return results.ToArray();
    }
    public static async Task<object> Probe(string selection)
    {
        using var track = CreateTrack("mic", selection);
        long frames = 0;
        float peak = 0;
        Exception? failure = null;
        track.Recorder.DataAvailable += (buffer, _, _, _) =>
        {
            Interlocked.Add(ref frames, buffer.Length / track.Info.BlockAlign);
            for (int i = 0; i < buffer.Length; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer.Slice(i, 4))));
        };
        track.Recorder.RecordingStopped += (_, args) => { failure = args.Exception; track.Stopped.TrySetResult(); };
        Console.WriteLine("Checking microphone levels for five seconds. Audio stays in memory and is not saved.");
        track.Recorder.StartRecording();
        try { await Task.Delay(5000); }
        finally { track.Recorder.StopRecording(); await track.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        if (failure != null) throw new IOException("Microphone probe failed.", failure);
        if (frames == 0) throw new IOException("No microphone frames received. Check source selection and Windows microphone permission.");
        return new { device = track.Info.Name, frames, peakDbfs = 20 * Math.Log10(Math.Max(peak, 0.00001f)), track.Info.SampleRate, track.Info.Channels, audioSaved = false };
    }

    public static object[] Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        List<object> result = [];
        foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
        {
            string? defaultId = null;
            try { using var selected = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); defaultId = selected.ID; }
            catch (System.Runtime.InteropServices.COMException) { }
            using var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var device in collection)
            {
                using (device) result.Add(new { kind = flow == DataFlow.Capture ? "mic" : "output", id = device.ID, name = device.FriendlyName, isDefault = device.ID == defaultId });
            }
        }
        return result.ToArray();
    }

    static CaptureTrack CreateTrack(string id, string selection)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = selection == "default"
            ? enumerator.GetDefaultAudioEndpoint(id == "mic" ? DataFlow.Capture : DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(selection);
        try
        {
            if (device.DataFlow != (id == "mic" ? DataFlow.Capture : DataFlow.Render)) throw new ArgumentException("Endpoint type does not match source.");
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithBufferLength(50)
                .WithMmcssThreadPriority("Audio").WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, id == "mic" ? 1 : 2));
            if (id == "output") builder.WithLoopbackCapture();
            var recorder = builder.Build();
            return new(new(id, device.ID, device.FriendlyName, 48000, id == "mic" ? 1 : 2, 32, "ieee-float"), recorder, device);
        }
        catch { device.Dispose(); throw; }
    }

    public static async Task<Inspection> Record(Vault vault, string? mic, string? output, int seconds, bool testProcess = false, string? liveInstall = null)
    {
        if (seconds is < 1 or > 3600) throw new ArgumentException("Prototype duration must be 1–3600 seconds.");
        List<CaptureTrack> tracks = [];
        if (testProcess)
        {
            var recorder = await new WasapiRecorderBuilder().WithProcessLoopback((uint)Environment.ProcessId)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).BuildAsync();
            tracks.Add(new(new("output", "self-test-process", "Generated tone in this process only", 48000, 2, 32, "ieee-float"), recorder, null));
        }
        else
        {
            try
            {
                if (mic != null) tracks.Add(CreateTrack("mic", mic));
                if (output != null) tracks.Add(CreateTrack("output", output));
            }
            catch { foreach (var track in tracks) track.Dispose(); throw; }
        }
        if (tracks.Count == 0) throw new ArgumentException("Select at least one source explicitly.");
        var session = new SessionInfo(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, ChunkPump.QpcNow(), Stopwatch.Frequency,
            "recording", liveInstall == null ? "not-configured" : "live-local-english", tracks.Select(track => track.Info).ToArray());
        ChunkPump? pump = null;
        LiveTranscription? live = null;
        int paused = 0, stopping = 0, cancelled = 0;
        string? error = null;
        int eventSequence = 0;
        void Event(string kind) => vault.Write($"sessions/{session.Id}/event-{eventSequence++:D8}.sne", new { version = 1, kind, qpc100ns = ChunkPump.QpcNow() });
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; Interlocked.Exchange(ref cancelled, 1); };
        try
        {
            pump = new ChunkPump(vault, session);
            if (liveInstall != null) { live = new(vault, session, liveInstall); live.Start(); }
            foreach (var track in tracks)
            {
                track.Recorder.DataAvailable += (buffer, flags, position, qpc) =>
                {
                    try
                    {
                        Interlocked.Exchange(ref track.LastCallback, ChunkPump.QpcNow());
                        float peak = 0;
                        for (int i = 0; i < buffer.Length; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer.Slice(i, 4))));
                        Volatile.Write(ref track.Peak, peak);
                        if (Volatile.Read(ref paused) != 0) return;
                        if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) Interlocked.Increment(ref track.Discontinuities);
                        if ((flags & AudioClientBufferFlags.TimestampError) != 0) Interlocked.Increment(ref track.TimestampErrors);
                        if (!pump.Offer(track.Info.Id, buffer, position, qpc, (int)flags))
                        { Interlocked.CompareExchange(ref error, "Capture queue capacity exceeded or persistence failed.", null); return; }
                        Interlocked.Add(ref track.Bytes, buffer.Length);
                    }
                    catch (Exception ex) { Interlocked.CompareExchange(ref error, ex.Message, null); }
                };
                track.Recorder.RecordingStopped += (_, args) =>
                {
                    if (args.Exception != null || Volatile.Read(ref stopping) == 0)
                        Interlocked.CompareExchange(ref error, args.Exception?.Message ?? "Audio source stopped unexpectedly.", null);
                    track.Stopped.TrySetResult();
                };
            }
            Console.WriteLine($"Recording session {session.Id}. Local encrypted storage only. Inference: {(live == null ? "not configured" : "live local English; audio persistence is independent")}.");
            foreach (var track in tracks) Console.WriteLine($"  {track.Info.Id}: {track.Info.Name}");
            Console.WriteLine("P pauses/resumes; Q or Ctrl+C stops. Fixed duration also stops recording.");
            Console.CancelKeyPress += cancel;
            foreach (var track in tracks) track.Recorder.StartRecording();
            Event("capture-started");
            using var player = testProcess ? new WasapiPlayerBuilder().Build() : null;
            if (player != null) { player.Init(new Tone(48000, 2, seconds - 1).ToWaveProvider()); player.Play(); }
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds && Volatile.Read(ref cancelled) == 0 && Volatile.Read(ref error) == null && pump.Failure == null)
            {
                if (File.Exists(Path.Combine(vault.SessionPath(session.Id), "stop-request.sne")))
                {
                    if (vault.Read($"sessions/{session.Id}/stop-request.sne").Metadata.GetProperty("kind").GetString() == "stop-request") break;
                }
                if (!Console.IsInputRedirected && Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    if (key == ConsoleKey.Q) break;
                    if (key == ConsoleKey.P) { Interlocked.Exchange(ref paused, 1 - Volatile.Read(ref paused)); Event(paused == 1 ? "paused" : "resumed"); }
                }
                Console.Write($"\r{(paused == 1 ? "PAUSED   " : "RECORDING")} {clock.Elapsed:mm\\:ss} ");
                foreach (var track in tracks)
                {
                    long last = Interlocked.Read(ref track.LastCallback);
                    string level = last == 0 || ChunkPump.QpcNow() - last > 15_000_000 ? "no callbacks" : $"{20 * Math.Log10(Math.Max(Volatile.Read(ref track.Peak), 0.00001f)),6:F1} dBFS";
                    Console.Write($"| {track.Info.Id} {level} ");
                }
                Console.Write($"| queued {pump.QueuedBytes / 1024} KiB    ");
                if (live != null) Console.Write($"| text {live.Status.State} {live.Status.Tracks.Sum(t => t.FinalPassages)} saved passages    ");
                await Task.Delay(500);
            }
            player?.Stop();
        }
        catch (Exception ex) { error ??= ex.Message; }
        finally
        {
            Interlocked.Exchange(ref stopping, 1);
            foreach (var track in tracks) track.Recorder.StopRecording();
            foreach (var track in tracks)
            {
                if (track.Recorder.CaptureState == CaptureState.Stopped) track.Stopped.TrySetResult();
                try { await track.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { error ??= "Audio source did not stop within five seconds."; }
                track.Dispose();
            }
            Console.CancelKeyPress -= cancel;
        }
        Console.WriteLine();
        if (pump == null) throw new IOException(error ?? "Cannot create capture session.");
        foreach (var track in tracks)
            if (track.Bytes == 0) error ??= $"No audio packets were received from {track.Info.Id}; the source is not validated.";
        foreach (var track in tracks)
            vault.Write($"sessions/{session.Id}/{track.Info.Id}-stats.snm", new { track.Bytes, track.Discontinuities, track.TimestampErrors });
        Event(error == null ? "capture-stopped" : "capture-interrupted");
        try { await pump.Finish(error ?? "User stop or fixed duration elapsed.", error == null); }
        finally { if (live != null) { await live.Finish(); Console.WriteLine($"Local live transcription: {live.Status.State}. {live.Status.Tracks.Sum(t => t.FinalPassages)} saved passages. No text is printed to logs."); } }
        var inspection = Archive.Inspect(vault, session.Id);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inspection, Vault.Json));
        if (testProcess && tracks.Sum(track => track.Bytes) == 0) throw new IOException("Loopback test received no audio; capture path is not validated.");
        return inspection;
    }

    sealed class Tone(int rate, int channels, int seconds) : ISampleProvider
    {
        long position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        public int Read(Span<float> buffer)
        {
            int length = (int)Math.Min(buffer.Length, (long)rate * channels * seconds - position);
            for (int i = 0; i < length; i++) buffer[i] = (float)(0.01 * Math.Sin(2 * Math.PI * 440 * ((position + i) / channels) / rate));
            position += length;
            return length;
        }
    }
}
