using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace SaidNest.Capture;

sealed record WindowChunk(string File, string Hash, double Start, double End);

// An index of authenticated, committed objects. Audio buffers are bounded by the requested window.
sealed class AudioWindows(Vault vault, SessionInfo session, TrackInfo track)
{
    readonly List<WindowChunk> chunks = [];
    long frames;
    public double Available { get; private set; }
    public int Count => chunks.Count;
    public void Poll()
    {
        if (track.SampleRate != 48000 || track.BitsPerSample != 32 || track.Encoding != "ieee-float" || track.Channels is < 1 or > 2) throw new InvalidDataException("Unsupported PCM format.");
        while (true)
        {
            string file = $"{track.Id}-{chunks.Count:D8}.snc";
            if (!File.Exists(Path.Combine(vault.SessionPath(session.Id), file))) break;
            var obj = vault.Read($"sessions/{session.Id}/{file}");
            try
            {
                var chunk = obj.Metadata.Deserialize<ChunkInfo>(Vault.Json)!;
                if (chunk.Version != 1 || chunk.Track != track.Id || chunk.Sequence != chunks.Count || chunk.FirstSample != frames || chunk.Frames * track.BlockAlign != obj.Audio.Length || chunk.Packets.Length == 0) throw new InvalidDataException("Invalid committed audio sequence.");
                int offset = 0;
                foreach (var packet in chunk.Packets)
                {
                    if (packet.ByteOffset != offset || packet.Frames <= 0) throw new InvalidDataException("Invalid packet range.");
                    offset = checked(offset + packet.Frames * track.BlockAlign);
                }
                if (offset != obj.Audio.Length) throw new InvalidDataException("Invalid packet coverage.");
                double start = (chunk.Packets.Min(p => p.Qpc100ns) - session.StartQpc100ns) / 10_000_000.0;
                double end = chunk.Packets.Max(p => (p.Qpc100ns - session.StartQpc100ns) / 10_000_000.0 + p.Frames / 48000.0);
                if (start < -1 || end > 3602 || end < start) throw new InvalidDataException("Implausible recording clock.");
                chunks.Add(new(file, obj.Hash, start, end)); frames += chunk.Frames; Available = Math.Max(Available, end);
            }
            finally { CryptographicOperations.ZeroMemory(obj.Audio); }
        }
    }

    public RenderedAudio Render(double start, double seconds)
    {
        if (!double.IsFinite(start) || !double.IsFinite(seconds) || start < 0 || seconds <= 0 || seconds > 60 || start + seconds > 3602) throw new ArgumentException("Select a recording window of up to sixty seconds.");
        int length = checked((int)Math.Round(seconds * 48000));
        byte[] wav = new byte[44 + length * 2];
        long startFrame = (long)Math.Round(start * 48000), written = startFrame, inserted = 0, trimmed = 0, count = 0;
        double peak = 0, squares = 0;
        foreach (var entry in chunks.Where(item => item.End > start && item.Start < start + seconds))
        {
            var obj = vault.Read($"sessions/{session.Id}/{entry.File}");
            try
            {
                if (obj.Hash != entry.Hash) throw new CryptographicException("Committed audio changed after indexing.");
                var chunk = obj.Metadata.Deserialize<ChunkInfo>(Vault.Json)!;
                foreach (var packet in chunk.Packets)
                {
                    long target = (long)Math.Round((packet.Qpc100ns - session.StartQpc100ns) * (48000 / 10_000_000.0));
                    if (target >= startFrame + length || target + packet.Frames <= startFrame) continue;
                    if ((packet.Flags & 4) != 0) throw new InvalidDataException("Hardware timestamp error prevents alignment.");
                    long gap = Math.Max(0, Math.Min(target, startFrame + length) - written); inserted += gap; written += gap;
                    int skip = (int)Math.Min(Math.Max(written - target, 0), packet.Frames);
                    trimmed += Math.Max(0, skip - Math.Max(0, startFrame - target));
                    int take = (int)Math.Min(packet.Frames - skip, startFrame + length - written);
                    for (int frame = skip; frame < skip + take; frame++)
                    {
                        double sample = 0;
                        for (int channel = 0; channel < track.Channels; channel++)
                        {
                            float value = BitConverter.ToSingle(obj.Audio, packet.ByteOffset + frame * track.BlockAlign + channel * 4);
                            if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite audio sample.");
                            sample += value / track.Channels;
                        }
                        peak = Math.Max(peak, Math.Abs(sample)); squares += sample * sample; count++;
                        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + checked((int)(written++ - startFrame)) * 2), (short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue));
                    }
                }
            }
            finally { CryptographicOperations.ZeroMemory(obj.Audio); }
        }
        inserted += Math.Max(0, startFrame + length - written);
        "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 48000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 96000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), length * 2);
        return new(wav, seconds, 20 * Math.Log10(Math.Max(peak, 1e-10)), 20 * Math.Log10(Math.Max(Math.Sqrt(squares / Math.Max(count, 1)), 1e-10)), inserted, trimmed);
    }
}
