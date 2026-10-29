using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaidNest.Capture;

sealed record EngineInstall(string Executable, string Model, string ModelSha256, string RuntimeBuild, string ModelRevision, string ModelName="unknown", int BeamSize=1, int BestOf=1);
sealed record TranscriptSegment(string Track, double Start, double End, string Text);
sealed record TrackTranscript(string Track, string State, double DurationSeconds, string Notice, TranscriptSegment[] Segments, int RejectedSegments=0);
sealed record TranscriptRevision(int Version, string Id, string SessionId, DateTimeOffset CreatedUtc, string Runtime, string ModelSha256, string ModelRevision, string Language, TrackTranscript[] Tracks, string Kind="legacy", string ModelName="unknown", string? ParentRevisionId=null, int WindowSeconds=0, string SourceManifestHash="",int BeamSize=1,int BestOf=1);
sealed record RefinementStatus(string State,string Model,double ProcessedSeconds,double TotalSeconds,DateTimeOffset UpdatedUtc,string Notice,DateTimeOffset? StartedUtc=null,double? WallSeconds=null);

static partial class Transcription
{
    [GeneratedRegex(@"^\[(\d{2}:\d{2}:\d{2}[.,]\d{3})\s+-->\s+(\d{2}:\d{2}:\d{2}[.,]\d{3})\]\s*(.*)$", RegexOptions.Multiline)]
    private static partial Regex SegmentLines();

    public static TranscriptRevision? Latest(Vault vault, string id)
    {
        var live = LiveTranscription.ReadTranscript(vault, id);
        var revisions=Revisions(vault,id);
        string? selected=File.Exists(Path.Combine(vault.SessionPath(id),"selected.sns")) ? vault.Read($"sessions/{id}/selected.sns").Metadata.GetProperty("id").GetString() : null;
        var saved=revisions.FirstOrDefault(r=>r.Id==selected) ?? revisions.FirstOrDefault();
        var status = LiveTranscription.ReadStatus(vault,id);
        return live != null && (status?.State != "failed" || saved == null || saved.CreatedUtc <= status.UpdatedUtc) ? live : saved;
    }

    public static TranscriptRevision[] Revisions(Vault vault,string id) => Directory.GetFiles(vault.SessionPath(id),"transcript-*.snt").OrderDescending(StringComparer.Ordinal)
        .Select(path=>vault.Read($"sessions/{id}/{Path.GetFileName(path)}").Metadata.Deserialize<TranscriptRevision>(Vault.Json)!).ToArray();
    public static void Select(Vault vault,string id,string revisionId)
    {
        using var selection=SelectionLock(vault,id);
        SelectUnlocked(vault,id,revisionId);
    }
    static FileStream SelectionLock(Vault vault,string id)
    {
        string path=Path.Combine(vault.SessionPath(id),"selection.lock");
        for(int attempt=0;;attempt++) {try {return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException)when(attempt<100){Thread.Sleep(10);}}
    }
    static void SelectUnlocked(Vault vault,string id,string revisionId)
    {
        if(!Revisions(vault,id).Any(r=>r.Id==revisionId)) throw new ArgumentException("Unknown transcript revision.");
        vault.Replace($"sessions/{id}/selected.sns",new {id=revisionId});
    }
    public static void SelectIfCurrent(Vault vault,string id,string? parent,string revisionId)
    {
        using var selection=SelectionLock(vault,id);
        if(Latest(vault,id)?.Id==parent)SelectUnlocked(vault,id,revisionId);
    }
    public static string Name(EngineInstall install)=>install.ModelName!="unknown"?install.ModelName:Path.GetFileNameWithoutExtension(install.Model).Replace("ggml-","");
    public static string Name(TranscriptRevision revision)=>revision.ModelName!="unknown"?revision.ModelName:revision.ModelSha256=="921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f"?"tiny.en":"Unlabeled model";
    public static RefinementStatus? ReadRefinement(Vault vault,string id)=>File.Exists(Path.Combine(vault.SessionPath(id),"refinement.snj"))?vault.Read($"sessions/{id}/refinement.snj").Metadata.Deserialize<RefinementStatus>(Vault.Json):null;

    public static TranscriptSegment[] Parse(string text, string track, double duration, out int rejected)
    {
        rejected = 0;
        List<TranscriptSegment> result = [];
        foreach (Match match in SegmentLines().Matches(text))
        {
            string content = match.Groups[3].Value.Trim();
            if (content is "" or "[BLANK_AUDIO]" or "[SILENCE]" or "(silence)") continue;
            double start = TimeSpan.Parse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture).TotalSeconds;
            double end = TimeSpan.Parse(match.Groups[2].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture).TotalSeconds;
            if (start < 0 || end <= start || start >= duration || end > duration + 0.25 || content.Length > 16_384) { rejected++; continue; }
            result.Add(new(track, start, Math.Min(end, duration), content));
        }
        return result.ToArray();
    }

    public static async Task<TranscriptRevision> Run(Vault vault, string id, string installFile, CancellationToken cancellationToken = default, Func<RenderedAudio,string,CancellationToken,Task<TrackTranscript>>? testDecoder=null)
    {
        if (Archive.Inspect(vault, id).State != "complete") throw new InvalidDataException("Transcription currently requires a finalized, verified recording.");
        using var priority = new ForegroundIntent(vault);
        using var lease = await InferencePriority.Acquire(vault,cancellationToken);
        var refinementClock=Stopwatch.StartNew(); var startedUtc=DateTimeOffset.UtcNow;
        var parent=Latest(vault,id);
        vault.Replace($"sessions/{id}/refinement.snj",new RefinementStatus("running","Loading local model",0,0,DateTimeOffset.UtcNow,"Preparing a separate refinement revision. Original transcripts are retained.",startedUtc,refinementClock.Elapsed.TotalSeconds));
        try {
        var install = testDecoder==null?await Load(installFile, cancellationToken):new EngineInstall("fixture","fixture","fixture","fixture","fixture","synthetic-refinement");
        List<TrackTranscript> tracks = [];
        var session = Archive.ReadSession(vault, id);
        var indexes=session.Tracks.ToDictionary(t=>t.Id,t=>new AudioWindows(vault,session,t));
        foreach(var index in indexes.Values) index.Poll();
        double total=indexes.Values.Sum(index=>index.Available), processed=0;
        foreach (var track in session.Tracks)
        {
            List<TranscriptSegment> parts=[]; int rejected=0;
            var index=indexes[track.Id];
            for(double start=0;start<index.Available-.02;start+=30) {
                cancellationToken.ThrowIfCancellationRequested();
                double duration=Math.Min(30,index.Available-start);
                var audio=index.Render(start,duration);
                var decoded=testDecoder==null?await Decode(install,audio,track.Id,cancellationToken):await testDecoder(audio,track.Id,cancellationToken);
                parts.AddRange(decoded.Segments.Select(s=>s with {Start=s.Start+start,End=s.End+start})); rejected+=decoded.RejectedSegments;
                processed+=duration;
                vault.Replace($"sessions/{id}/refinement.snj",new RefinementStatus("running",Name(install),processed,total,DateTimeOffset.UtcNow,"Creating a new local transcript revision; earlier text remains available.",startedUtc,refinementClock.Elapsed.TotalSeconds));
            }
            tracks.Add(new(track.Id,parts.Count>0?"transcribed":"no-text",index.Available,rejected>0?$"Local refinement discarded {rejected} invalid timestamp segments. Check the transcript against audio.":"Local thirty-second refinement windows. Review wording against audio.",parts.ToArray(),rejected));
        }
        var revision = new TranscriptRevision(1, Guid.NewGuid().ToString("N"), id, DateTimeOffset.UtcNow, $"whisper.cpp {install.RuntimeBuild} CUDA", install.ModelSha256, install.ModelRevision, "en", tracks.ToArray(),"refined",Name(install),parent?.Id,30,vault.Read($"sessions/{id}/final.snm").Hash,install.BeamSize,install.BestOf);
        string file = $"transcript-{revision.CreatedUtc:yyyyMMddTHHmmssfffffffZ}-{revision.Id}.snt";
        vault.Write($"sessions/{id}/{file}", revision);
        Select(vault,id,revision.Id);
        vault.Replace($"sessions/{id}/refinement.snj",new RefinementStatus("complete",Name(install),total,total,DateTimeOffset.UtcNow,"Refinement saved as a new encrypted revision. Earlier text is retained.",startedUtc,refinementClock.Elapsed.TotalSeconds));
        return revision;
        } catch {
            vault.Replace($"sessions/{id}/refinement.snj",new RefinementStatus("failed","Local model",0,0,DateTimeOffset.UtcNow,"Refinement stopped. Earlier transcripts and audio are retained.",startedUtc,refinementClock.Elapsed.TotalSeconds));
            throw;
        }
    }

    public static async Task<EngineInstall> Load(string installFile, CancellationToken cancellationToken = default)
    {
        var install = JsonSerializer.Deserialize<EngineInstall>(await File.ReadAllBytesAsync(installFile, cancellationToken), Vault.Json) ?? throw new InvalidDataException("Missing inference settings.");
        if (!File.Exists(install.Executable)) throw new FileNotFoundException("Provision the local inference runtime first.");
        if(install.BeamSize is <1 or >5 || install.BestOf is <1 or >5) throw new InvalidDataException("Unsupported local decoding settings.");
        await using (var model = File.OpenRead(install.Model))
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(model, cancellationToken)), install.ModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Local model checksum does not match its provisioned revision.");
        return install;
    }

    public static async Task<TrackTranscript> Decode(EngineInstall install, RenderedAudio audio, string trackId, CancellationToken cancellationToken = default, bool bypassQuietGuard=false,bool background=false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep quiet audio, but do not turn a low-level signal into a confident speech claim.
            if (!bypassQuietGuard && audio.RmsDbfs < -60 && audio.PeakDbfs < -45)
            {
                return new(trackId, "too-quiet", audio.DurationSeconds, "Signal is below this prototype's analysis threshold. Audio is retained; no transcript was inferred.", []);
            }
            var settings = new ProcessStartInfo(install.Executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(install.Executable)!
            };
            // A non-dash output basename enables the segment stdout callback with stdin input.
            // No --output-* format is selected, so the pinned CLI never creates a transcript file.
            foreach (string argument in new[] { "--model", install.Model, "--file", "-", "--output-file", "saidnest-memory-only", "--language", "en", "--threads", "8", "--best-of", install.BestOf.ToString(), "--beam-size", install.BeamSize.ToString(), "--temperature", "0", "--temperature-inc", "0", "--suppress-nst" }) settings.ArgumentList.Add(argument);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var process = Process.Start(settings) ?? throw new IOException("Local inference process did not start.");
            using var ownership=background?GpuOwnership.Register(process.Id):null;
            if(background) {try {process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(System.ComponentModel.Win32Exception) { }}
            try
            {
                var stdout = ReadLimited(process.StandardOutput, 2 * 1024 * 1024, timeout.Token);
                var stderr = ReadLimited(process.StandardError, 1024 * 1024, timeout.Token);
                await process.StandardInput.BaseStream.WriteAsync(audio.Wav, timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                string output = await stdout;
                string backendLog = await stderr;
                if (process.ExitCode != 0) throw new IOException("Local inference failed; no transcript revision was committed.");
                if (!Regex.IsMatch(backendLog,@"whisper_backend_init_gpu: using CUDA\d+ backend") || !Regex.IsMatch(backendLog,@"CUDA\d+ total size")) throw new IOException("CUDA execution was not established; no silent backend switch is allowed in this worker.");
                var segments = Parse(output, trackId, audio.DurationSeconds, out int rejected);
                return new(trackId, segments.Length > 0 ? "transcribed" : "no-text", audio.DurationSeconds,
                    rejected > 0 ? $"{rejected} invalid timestamp segments were rejected. Review this experimental transcript." : $"Experimental local {Name(install)} English transcription. Check important words against the audio.", segments,rejected);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
        }
        finally { CryptographicOperations.ZeroMemory(audio.Wav); }
    }

    static async Task<string> ReadLimited(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + count > limit) throw new InvalidDataException("Inference output exceeded the local worker limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
