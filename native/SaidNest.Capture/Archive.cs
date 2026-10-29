using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;

namespace SaidNest.Capture;

sealed record TrackInfo(string Id, string DeviceId, string Name, int SampleRate, int Channels, int BitsPerSample, string Encoding)
{
    public int BlockAlign => Channels * BitsPerSample / 8;
}
sealed record SessionInfo(int Version, string Id, DateTimeOffset StartedUtc, long StartQpc100ns, long QpcFrequency, string CaptureState, string InferenceState, TrackInfo[] Tracks);
sealed record Timing(int ByteOffset, int Frames, long DevicePosition, long Qpc100ns, long Arrival100ns, int Flags);
sealed record Packet(string Track, byte[] Data, Timing Timing);
sealed record ChunkInfo(int Version, string Track, int Sequence, long FirstSample, int Frames, Timing[] Packets);
sealed record ChunkRef(string File, string Track, int Sequence, int Frames, string Hash);
sealed record FinalInfo(int Version, string State, DateTimeOffset StoppedUtc, string Reason, ChunkRef[] Chunks, long DroppedBytes);
sealed record Inspection(string Id, string State, int VerifiedChunks, long VerifiedFrames, int OrphanPartials, string[] Issues);

sealed class ChunkPump
{
    readonly Vault vault;
    readonly SessionInfo session;
    readonly long capacity;
    readonly Channel<Packet> queue = Channel.CreateBounded<Packet>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly List<ChunkRef> committed = [];
    readonly Dictionary<string, MemoryStream> buffers = [];
    readonly Dictionary<string, List<Timing>> timings = [];
    readonly Dictionary<string, int> sequence = [];
    readonly Dictionary<string, long> samples = [];
    readonly Task writer;
    readonly FileStream lease;
    long queuedBytes;
    long droppedBytes;
    int stopped;
    Exception? failure;
    public Exception? Failure => Volatile.Read(ref failure);
    public long DroppedBytes => Interlocked.Read(ref droppedBytes);
    public long QueuedBytes => Interlocked.Read(ref queuedBytes);

    public ChunkPump(Vault vault, SessionInfo session, long capacity = 8 * 1024 * 1024)
    {
        this.vault = vault; this.session = session; this.capacity = capacity;
        Directory.CreateDirectory(vault.SessionPath(session.Id));
        lease = new FileStream(Path.Combine(vault.SessionPath(session.Id), "capture.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        vault.Write($"sessions/{session.Id}/session.snm", session);
        foreach (var track in session.Tracks) { buffers[track.Id] = new(); timings[track.Id] = []; sequence[track.Id] = 0; samples[track.Id] = 0; }
        writer = Task.Run(Consume);
    }

    // Reserve memory before copying WASAPI's short-lived buffer. Neither disk nor inference runs here.
    public bool Offer(string track, ReadOnlySpan<byte> data, long devicePosition, long qpc, int flags)
    {
        if (Volatile.Read(ref stopped) != 0 || Failure != null) return false;
        if (data.Length == 0) return true;
        if (data.Length > 1024 * 1024 || Interlocked.Add(ref queuedBytes, data.Length) > capacity)
        {
            if (data.Length <= 1024 * 1024) Interlocked.Add(ref queuedBytes, -data.Length);
            Interlocked.Add(ref droppedBytes, data.Length);
            Interlocked.Exchange(ref stopped, 1);
            return false;
        }
        var info = session.Tracks.Single(item => item.Id == track);
        if (data.Length % info.BlockAlign != 0) throw new InvalidDataException("Partial PCM frame.");
        var packet = new Packet(track, data.ToArray(), new Timing(0, data.Length / info.BlockAlign, devicePosition, qpc, QpcNow(), flags));
        if (queue.Writer.TryWrite(packet)) return true;
        Interlocked.Add(ref queuedBytes, -data.Length);
        Interlocked.Add(ref droppedBytes, data.Length);
        return false;
    }

    async Task Consume()
    {
        try
        {
            await foreach (var packet in queue.Reader.ReadAllAsync())
            {
                var buffer = buffers[packet.Track];
                timings[packet.Track].Add(packet.Timing with { ByteOffset = checked((int)buffer.Length) });
                buffer.Write(packet.Data);
                Interlocked.Add(ref queuedBytes, -packet.Data.Length);
                var track = session.Tracks.Single(item => item.Id == packet.Track);
                if (buffer.Length >= track.SampleRate * track.BlockAlign) Flush(track);
            }
            foreach (var track in session.Tracks) Flush(track);
        }
        catch (Exception ex) { Volatile.Write(ref failure, ex); Interlocked.Exchange(ref stopped, 1); }
        finally { foreach (var buffer in buffers.Values) buffer.Dispose(); }
    }

    void Flush(TrackInfo track)
    {
        var buffer = buffers[track.Id];
        if (buffer.Length == 0) return;
        int frames = checked((int)buffer.Length / track.BlockAlign);
        int number = sequence[track.Id]++;
        string file = $"{track.Id}-{number:D8}.snc";
        var meta = new ChunkInfo(1, track.Id, number, samples[track.Id], frames, timings[track.Id].ToArray());
        string hash = vault.Write($"sessions/{session.Id}/{file}", meta, buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        committed.Add(new(file, track.Id, number, frames, hash));
        samples[track.Id] += frames;
        timings[track.Id].Clear();
        buffer.SetLength(0);
    }

    public async Task<FinalInfo> Finish(string reason, bool healthy)
    {
        Interlocked.Exchange(ref stopped, 1);
        queue.Writer.TryComplete();
        await writer;
        try
        {
            if (Failure != null) throw new IOException("Persistence stopped; inspect the session to recover committed chunks.", Failure);
            var final = new FinalInfo(1, healthy && DroppedBytes == 0 ? "complete" : "interrupted", DateTimeOffset.UtcNow, reason, committed.ToArray(), DroppedBytes);
            vault.Write($"sessions/{session.Id}/final.snm", final);
            return final;
        }
        finally { lease.Dispose(); }
    }

    public static long QpcNow() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
}

static class Archive
{
    public static SessionInfo ReadSession(Vault vault, string id) => vault.Read($"sessions/{id}/session.snm").Metadata.Deserialize<SessionInfo>(Vault.Json) ?? throw new InvalidDataException("Missing session.");

    public static Inspection Inspect(Vault vault, string id)
    {
        string folder = vault.SessionPath(id);
        List<string> issues = [];
        int count = 0;
        long frames = 0;
        try
        {
            bool active = false;
            if (File.Exists(Path.Combine(folder, "capture.lock")))
            {
                try { using var unused = new FileStream(Path.Combine(folder, "capture.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { active = true; }
            }
            var session = ReadSession(vault, id);
            Dictionary<string, long> nextSample = session.Tracks.ToDictionary(track => track.Id, _ => 0L);
            Dictionary<string, int> nextSequence = session.Tracks.ToDictionary(track => track.Id, _ => 0);
            List<ChunkRef> actual = [];
            int discontinuities = 0, timestampErrors = 0;
            foreach (string path in Directory.EnumerateFiles(folder, "*.snc").Order(StringComparer.Ordinal))
            {
                string file = Path.GetFileName(path);
                var obj = vault.Read($"sessions/{id}/{file}");
                var chunk = obj.Metadata.Deserialize<ChunkInfo>(Vault.Json) ?? throw new InvalidDataException("Missing chunk metadata.");
                var track = session.Tracks.Single(item => item.Id == chunk.Track);
                if (chunk.Version != 1 || chunk.Sequence != nextSequence[track.Id]++ || chunk.FirstSample != nextSample[track.Id] ||
                    chunk.Frames * track.BlockAlign != obj.Audio.Length || file != $"{track.Id}-{chunk.Sequence:D8}.snc")
                    throw new InvalidDataException("Chunk sequence, name, or sample range does not match.");
                int offset = 0;
                foreach (var packet in chunk.Packets)
                {
                    if (packet.ByteOffset != offset || packet.Frames <= 0) throw new InvalidDataException("Invalid packet index.");
                    offset = checked(offset + packet.Frames * track.BlockAlign);
                    if ((packet.Flags & 1) != 0) discontinuities++;
                    if ((packet.Flags & 4) != 0) timestampErrors++;
                }
                if (offset != obj.Audio.Length) throw new InvalidDataException("Packet index does not cover audio.");
                nextSample[track.Id] += chunk.Frames;
                actual.Add(new(file, track.Id, chunk.Sequence, chunk.Frames, obj.Hash));
                count++; frames += chunk.Frames;
            }
            string state = "interrupted";
            if (!active && File.Exists(Path.Combine(folder, "final.snm")))
            {
                var final = vault.Read($"sessions/{id}/final.snm").Metadata.Deserialize<FinalInfo>(Vault.Json)!;
                if (!final.Chunks.OrderBy(item => item.File, StringComparer.Ordinal).SequenceEqual(actual.OrderBy(item => item.File, StringComparer.Ordinal)))
                    throw new InvalidDataException("Final manifest and committed chunks differ; data may be missing.");
                state = final.State;
            }
            else
            {
                if (active) state = "recording";
                issues.Add(active ? "Capture is still active; this is a snapshot of committed chunks." : "No final manifest: committed chunks are recoverable; completeness is not established.");
            }
            if (discontinuities > 0) issues.Add($"{discontinuities} WASAPI discontinuity flags retained in packet timing; gap-free capture is not established.");
            if (timestampErrors > 0) issues.Add($"{timestampErrors} invalid timestamp flags; aligned export is blocked for affected packets.");
            return new(id, state, count, frames, Directory.GetFiles(folder, "*.partial").Length, issues.ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Security.Cryptography.CryptographicException or InvalidOperationException or JsonException)
        { issues.Add(ex.Message); return new(id, "corrupt", count, frames, 0, issues.ToArray()); }
    }

    public static void Export(Vault vault, string id, string trackId, string output)
    {
        output = Path.GetFullPath(output);
        if (output.StartsWith(vault.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Plaintext exports must be outside the encrypted vault.");
        if (File.Exists(output)) throw new IOException("Export never overwrites a file.");
        var check = Inspect(vault, id);
        if (check.State == "corrupt") throw new InvalidDataException("Refusing to export corrupt data.");
        var session = ReadSession(vault, id);
        var track = session.Tracks.Single(item => item.Id == trackId);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new WaveFileWriter(file, WaveFormat.CreateIeeeFloatWaveFormat(track.SampleRate, track.Channels));
        long written = 0;
        byte[] silence = new byte[track.SampleRate * track.BlockAlign];
        foreach (string path in Directory.EnumerateFiles(vault.SessionPath(id), trackId + "-*.snc").Order(StringComparer.Ordinal))
        {
            var obj = vault.Read($"sessions/{id}/{Path.GetFileName(path)}");
            var chunk = obj.Metadata.Deserialize<ChunkInfo>(Vault.Json)!;
            foreach (var packet in chunk.Packets)
            {
                // WASAPI QPC is in 100ns units. Timestamp-error packets cannot be placed authoritatively.
                if ((packet.Flags & 4) != 0) throw new InvalidDataException("Timestamp-error packet; export requires a reviewed alignment policy.");
                long target = (long)Math.Round((packet.Qpc100ns - session.StartQpc100ns) * (track.SampleRate / 10_000_000.0));
                if (target - written > track.SampleRate * 3600L) throw new InvalidDataException("Implausible timestamp gap.");
                while (target > written)
                {
                    int pad = (int)Math.Min(target - written, track.SampleRate);
                    writer.Write(silence, 0, pad * track.BlockAlign); written += pad;
                }
                int skip = (int)Math.Min(Math.Max(written - target, 0), packet.Frames);
                int remaining = packet.Frames - skip;
                writer.Write(obj.Audio, packet.ByteOffset + skip * track.BlockAlign, remaining * track.BlockAlign);
                written += remaining;
            }
        }
    }
}
