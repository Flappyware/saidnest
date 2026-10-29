using System.Diagnostics;
using System.Text.Json;

namespace SaidNest.Capture;

sealed record LivePart(int Version, string Track, int Window, bool Final, double Start, double End, DateTimeOffset CreatedUtc, TrackTranscript Transcript);
sealed record LiveProgress(string Track, double FinalizedSeconds, double AvailableSeconds, int FinalPassages, int ProvisionalPassages, double PeakDbfs, double RmsDbfs, double LastInferenceSeconds);
sealed record LiveStatus(int Version, string SessionId, string State, DateTimeOffset UpdatedUtc, string Notice, LiveProgress[] Tracks, string Runtime="", string ModelSha256="", string ModelRevision="");

sealed class LiveTranscription(Vault vault, SessionInfo session, string installFile, Func<RenderedAudio, string, CancellationToken, Task<TrackTranscript>>? testDecoder = null)
{
    const int Window = 8, Update = 4;
    int finishing;
    readonly CancellationTokenSource cancellation = new();
    Task? task;
    EngineInstall? engine;
    volatile LiveStatus current = new(1, session.Id, "starting", DateTimeOffset.UtcNow, "Starting experimental local English transcription.", []);
    public LiveStatus Status => current;
    public void Start() => task = Task.Run(Run);
    public async Task Finish()
    {
        Interlocked.Exchange(ref finishing, 1);
        try { if (task != null) await task.WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { cancellation.Cancel(); if (task != null) await task; }
        finally { cancellation.Dispose(); }
    }
    void SaveStatus(string state, string notice, LiveProgress[] progress)
    {
        current = new(1, session.Id, state, DateTimeOffset.UtcNow, notice, progress, engine?.RuntimeBuild ?? "not-loaded", engine?.ModelSha256 ?? "not-loaded", engine?.ModelRevision ?? "not-loaded");
        // Atomic encrypted replacement is a current-progress checkpoint, not transcript history.
        vault.Replace($"sessions/{session.Id}/live-status.snl", current);
    }
    async Task Run()
    {
        var progress = session.Tracks.ToDictionary(t => t.Id, t => new LiveProgress(t.Id, 0, 0, 0, 0, -200, -200, 0));
        try
        {
            using var priority = new ForegroundIntent(vault);
            using var lease = await InferencePriority.Acquire(vault,cancellation.Token);
            var install = testDecoder == null ? await Transcription.Load(installFile, cancellation.Token) : new EngineInstall("fixture", "fixture", "fixture", "fixture", "fixture");
            engine = install;
            var indexes = session.Tracks.ToDictionary(t => t.Id, t => new AudioWindows(vault, session, t));
            var next = session.Tracks.ToDictionary(t => t.Id, _ => 0);
            var provisional = session.Tracks.ToDictionary(t => t.Id, _ => -1);
            SaveStatus("running", "Updates about every four seconds. The current eight-second window is provisional; window boundaries can affect wording.", progress.Values.ToArray());
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                bool didWork = false;
                foreach (var track in session.Tracks)
                {
                    var index = indexes[track.Id]; index.Poll();
                    double begin = next[track.Id] * Window;
                    bool done = Volatile.Read(ref finishing) != 0;
                    bool final = index.Available >= begin + Window || done;
                    double end = final ? Math.Min(begin + Window, index.Available) : begin + Update;
                    if (end <= begin + .02 || (!final && (index.Available < end || provisional[track.Id] == next[track.Id]))) continue;
                    var clock = Stopwatch.StartNew();
                    var audio = index.Render(begin, end - begin);
                    double peak = audio.PeakDbfs, rms = audio.RmsDbfs;
                    var decoded = testDecoder == null ? await Transcription.Decode(install, audio, track.Id, cancellation.Token) : await testDecoder(audio, track.Id, cancellation.Token);
                    var shifted = decoded with { Segments = decoded.Segments.Select(s => s with { Start = s.Start + begin, End = s.End + begin }).ToArray() };
                    var part = new LivePart(1, track.Id, next[track.Id], final, begin, end, DateTimeOffset.UtcNow, shifted);
                    vault.Write($"sessions/{session.Id}/live-{track.Id}-{part.Window:D8}-{(final ? "final" : "draft")}.snt", part);
                    var previous = progress[track.Id];
                    progress[track.Id] = new(track.Id, final ? end : previous.FinalizedSeconds, index.Available,
                        previous.FinalPassages + (final ? shifted.Segments.Length : 0), final ? 0 : shifted.Segments.Length, peak, rms, clock.Elapsed.TotalSeconds);
                    if (final) { next[track.Id]++; provisional[track.Id] = -1; } else provisional[track.Id] = next[track.Id];
                    didWork = true;
                    SaveStatus(done ? "finishing" : "running", "Local rolling transcription. Current draft may change; saved audio is independent.", progress.Values.ToArray());
                }
                if (Volatile.Read(ref finishing) != 0 && indexes.All(pair => next[pair.Key] * Window >= pair.Value.Available - .02)) break;
                if (!didWork) await Task.Delay(250, cancellation.Token);
            }
            var revision = Compose(vault, session.Id, install);
            vault.Write($"sessions/{session.Id}/transcript-{revision.CreatedUtc:yyyyMMddTHHmmssfffffffZ}-{revision.Id}.snt", revision);
            SaveStatus("complete", "Local rolling transcription finished. Review words against the saved audio; eight-second window boundaries may affect recognition.", progress.Values.ToArray());
        }
        catch (Exception)
        {
            string notice="Live transcription stopped. Committed passages and audio are retained; capture continues independently.";
            try { SaveStatus("failed",notice,progress.Values.ToArray()); }
            catch (Exception) { current = current with { State="failed", Notice=notice, UpdatedUtc=DateTimeOffset.UtcNow }; }
        }
    }
    public static LiveStatus? ReadStatus(Vault vault, string id) => File.Exists(Path.Combine(vault.SessionPath(id), "live-status.snl"))
        ? vault.Read($"sessions/{id}/live-status.snl").Metadata.Deserialize<LiveStatus>(Vault.Json) : null;

    public static TranscriptRevision? ReadTranscript(Vault vault, string id)
    {
        var status = ReadStatus(vault, id);
        return status == null || status.State == "complete" ? null : Compose(vault, id, null);
    }
    public static TranscriptRevision Compose(Vault vault, string id, EngineInstall? install)
    {
        var session = Archive.ReadSession(vault, id);
        var parts = Directory.GetFiles(vault.SessionPath(id), "live-*.snt").Select(path => vault.Read($"sessions/{id}/{Path.GetFileName(path)}").Metadata.Deserialize<LivePart>(Vault.Json)!)
            .GroupBy(p => (p.Track, p.Window)).Select(g => g.OrderByDescending(p => p.Final).First()).ToArray();
        var tracks = session.Tracks.Select(track => {
            var selected = parts.Where(p => p.Track == track.Id).OrderBy(p => p.Window).ToArray();
            return new TrackTranscript(track.Id, selected.Any(p => !p.Final) ? "provisional" : selected.Any(p=>p.Transcript.Segments.Length>0) ? "transcribed" : "no-text", selected.LastOrDefault()?.End ?? 0,
                "Experimental local English rolling transcription. Current draft may change; check wording at window boundaries.", selected.SelectMany(p => p.Transcript.Segments).ToArray());
        }).ToArray();
        var status = ReadStatus(vault,id);
        return new(1, Guid.NewGuid().ToString("N"), id, DateTimeOffset.UtcNow, $"whisper.cpp {install?.RuntimeBuild ?? status?.Runtime} CUDA rolling", install?.ModelSha256 ?? status?.ModelSha256 ?? "unknown", install?.ModelRevision ?? status?.ModelRevision ?? "unknown", "en", tracks,"live",install==null?"unknown":Transcription.Name(install),WindowSeconds:8);
    }
}
