using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaidNest.Capture;

sealed record EngineInstall(string Executable, string Model, string ModelSha256, string RuntimeBuild, string ModelRevision);
sealed record TranscriptSegment(string Track, double Start, double End, string Text);
sealed record TrackTranscript(string Track, string State, double DurationSeconds, string Notice, TranscriptSegment[] Segments);
sealed record TranscriptRevision(int Version, string Id, string SessionId, DateTimeOffset CreatedUtc, string Runtime, string ModelSha256, string ModelRevision, string Language, TrackTranscript[] Tracks);

static partial class Transcription
{
    [GeneratedRegex(@"^\[(\d{2}:\d{2}:\d{2}[.,]\d{3})\s+-->\s+(\d{2}:\d{2}:\d{2}[.,]\d{3})\]\s*(.*)$", RegexOptions.Multiline)]
    private static partial Regex SegmentLines();

    public static TranscriptRevision? Latest(Vault vault, string id)
    {
        var path = Directory.GetFiles(vault.SessionPath(id), "transcript-*.snt").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        return path == null ? null : vault.Read($"sessions/{id}/{Path.GetFileName(path)}").Metadata.Deserialize<TranscriptRevision>(Vault.Json);
    }

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

    public static async Task<TranscriptRevision> Run(Vault vault, string id, string installFile, CancellationToken cancellationToken = default)
    {
        if (Archive.Inspect(vault, id).State != "complete") throw new InvalidDataException("Transcription currently requires a finalized, verified recording.");
        using var lease = new FileStream(Path.Combine(vault.Root, "inference.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var install = JsonSerializer.Deserialize<EngineInstall>(await File.ReadAllBytesAsync(installFile, cancellationToken), Vault.Json) ?? throw new InvalidDataException("Missing inference settings.");
        if (!File.Exists(install.Executable)) throw new FileNotFoundException("Provision the local inference runtime first.");
        await using (var model = File.OpenRead(install.Model))
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(model, cancellationToken)), install.ModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Local model checksum does not match its provisioned revision.");
        List<TrackTranscript> tracks = [];
        var session = Archive.ReadSession(vault, id);
        foreach (var track in session.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var audio = TimelineAudio.Render(vault, id, track.Id);
            // Keep quiet audio, but do not turn a low-level signal into a confident speech claim.
            if (audio.RmsDbfs < -60 && audio.PeakDbfs < -45)
            {
                tracks.Add(new(track.Id, "too-quiet", audio.DurationSeconds, "Signal is below this prototype's analysis threshold. Audio is retained; no transcript was inferred.", []));
                CryptographicOperations.ZeroMemory(audio.Wav);
                continue;
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
            foreach (string argument in new[] { "--model", install.Model, "--file", "-", "--output-file", "saidnest-memory-only", "--language", "en", "--threads", "8", "--best-of", "1", "--beam-size", "1", "--temperature", "0", "--temperature-inc", "0", "--suppress-nst" }) settings.ArgumentList.Add(argument);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            using var process = Process.Start(settings) ?? throw new IOException("Local inference process did not start.");
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
                if (!backendLog.Contains("using CUDA", StringComparison.Ordinal)) throw new IOException("CUDA execution was not established; no silent backend switch is allowed in this worker.");
                var segments = Parse(output, track.Id, audio.DurationSeconds, out int rejected);
                tracks.Add(new(track.Id, segments.Length > 0 ? "transcribed" : "no-text", audio.DurationSeconds,
                    rejected > 0 ? $"{rejected} invalid timestamp segments were rejected. Review this experimental transcript." : "Experimental tiny English model. Check important words against the audio.", segments));
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            finally { CryptographicOperations.ZeroMemory(audio.Wav); }
        }
        var revision = new TranscriptRevision(1, Guid.NewGuid().ToString("N"), id, DateTimeOffset.UtcNow, $"whisper.cpp {install.RuntimeBuild} CUDA", install.ModelSha256, install.ModelRevision, "en", tracks.ToArray());
        string file = $"transcript-{revision.CreatedUtc:yyyyMMddTHHmmssfffffffZ}-{revision.Id}.snt";
        vault.Write($"sessions/{id}/{file}", revision);
        return revision;
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
