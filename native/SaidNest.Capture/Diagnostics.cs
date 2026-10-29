using System.Text.Json;

namespace SaidNest.Capture;

sealed record ControlEvent(string Kind, double Seconds);
sealed record PacketIssue(string Track, double Seconds, int Flags, double DeviceGapSeconds, double ClockGapSeconds, string Context);
sealed record TrackDiagnostic(string Track, int Chunks, long Frames, double AudioSeconds, double FirstPacketSeconds, double LastPacketSeconds, double PeakDbfs, double RmsDbfs, long NonFiniteSamples, PacketIssue[] Issues);
sealed record DiagnosticReport(string Id, string Integrity, double WallSeconds, ControlEvent[] Events, TrackDiagnostic[] Tracks);

static class Diagnostics
{
    public static DiagnosticReport Analyze(Vault vault, string id)
    {
        var inspection = Archive.Inspect(vault, id);
        if (inspection.State is "corrupt" or "recording") throw new InvalidDataException("Diagnosis requires an intact, stopped session.");
        var session = Archive.ReadSession(vault, id);
        string relative = $"sessions/{id}/";
        double Seconds(long qpc) => (qpc - session.StartQpc100ns) / 10_000_000.0;
        var events = Directory.GetFiles(vault.SessionPath(id), "*.sne").Order(StringComparer.Ordinal).Select(path =>
        {
            var value = vault.Read(relative + Path.GetFileName(path)).Metadata;
            return new ControlEvent(value.GetProperty("kind").GetString()!, Seconds(value.GetProperty("qpc100ns").GetInt64()));
        }).ToArray();
        double wall = events.LastOrDefault(item => item.Kind is "capture-stopped" or "capture-interrupted")?.Seconds ?? 0;
        List<TrackDiagnostic> results = [];
        foreach (var track in session.Tracks)
        {
            long frames = 0, sampleCount = 0, nonFinite = 0;
            double first = 0, end = 0, squares = 0, peak = 0;
            Timing? previous = null;
            List<PacketIssue> issues = [];
            int chunks = 0;
            foreach (string file in Directory.GetFiles(vault.SessionPath(id), track.Id + "-*.snc").Order(StringComparer.Ordinal))
            {
                var obj = vault.Read(relative + Path.GetFileName(file));
                var chunk = obj.Metadata.Deserialize<ChunkInfo>(Vault.Json)!;
                chunks++; frames += chunk.Frames;
                for (int index = 0; index < obj.Audio.Length; index += 4)
                {
                    float value = BitConverter.ToSingle(obj.Audio, index);
                    if (!float.IsFinite(value)) { nonFinite++; continue; }
                    peak = Math.Max(peak, Math.Abs(value)); squares += (double)value * value; sampleCount++;
                }
                foreach (var packet in chunk.Packets)
                {
                    double start = Seconds(packet.Qpc100ns);
                    double deviceGap = previous == null ? 0 : (packet.DevicePosition - previous.DevicePosition - previous.Frames) / (double)track.SampleRate;
                    double clockGap = previous == null ? 0 : (packet.Qpc100ns - previous.Qpc100ns) / 10_000_000.0 - previous.Frames / (double)track.SampleRate;
                    if (previous == null) first = start;
                    if ((packet.Flags & 5) != 0 || Math.Abs(deviceGap) > 0.002 || Math.Abs(clockGap) > 0.02)
                    {
                        string context = previous == null ? "startup" : "unexplained";
                        var pause = events.LastOrDefault(item => item.Kind == "paused" && item.Seconds >= (previous == null ? 0 : Seconds(previous.Qpc100ns)) - 0.1 && item.Seconds <= start + 0.1);
                        if (pause != null && events.Any(item => item.Kind == "resumed" && item.Seconds >= pause.Seconds && item.Seconds <= start + 0.1)) context = "manual-pause-resume";
                        issues.Add(new(track.Id, start, packet.Flags, deviceGap, clockGap, context));
                    }
                    previous = packet; end = start + packet.Frames / (double)track.SampleRate;
                }
            }
            results.Add(new(track.Id, chunks, frames, frames / (double)track.SampleRate, first, end,
                20 * Math.Log10(Math.Max(peak, 1e-10)), 20 * Math.Log10(Math.Max(Math.Sqrt(squares / Math.Max(sampleCount, 1)), 1e-10)), nonFinite, issues.ToArray()));
        }
        return new(id, inspection.State, wall, events, results.ToArray());
    }
}
