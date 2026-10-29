using System.Buffers.Binary;
using System.Text.Json;
using NAudio.Wave;

namespace SaidNest.Capture;

sealed record RenderedAudio(byte[] Wav, double DurationSeconds, double PeakDbfs, double RmsDbfs, long InsertedSilenceFrames, long TrimmedOverlapFrames);

static class TimelineAudio
{
    public const int MaxSeconds = 600;
    public static RenderedAudio Render(Vault vault, string id, string trackId)
    {
        var integrity = Archive.Inspect(vault, id);
        if (integrity.State is "corrupt" or "recording") throw new InvalidDataException("Playback requires an intact, stopped session.");
        var session = Archive.ReadSession(vault, id);
        var track = session.Tracks.Single(item => item.Id == trackId);
        if (track.SampleRate != 48000 || track.BitsPerSample != 32 || track.Encoding != "ieee-float" || track.Channels is < 1 or > 2)
            throw new InvalidDataException("Unsupported prototype PCM format.");
        using var pcm = new MemoryStream();
        long writtenFrames = 0, inserted = 0, trimmed = 0, sampleCount = 0;
        double peak = 0, squares = 0;
        byte[] silence = new byte[48000 * 2];
        foreach (string file in Directory.GetFiles(vault.SessionPath(id), trackId + "-*.snc").Order(StringComparer.Ordinal))
        {
            var obj = vault.Read($"sessions/{id}/{Path.GetFileName(file)}");
            var chunk = obj.Metadata.Deserialize<ChunkInfo>(Vault.Json)!;
            foreach (var packet in chunk.Packets)
            {
                if ((packet.Flags & 4) != 0) throw new InvalidDataException("Packet has an invalid hardware timestamp; aligned playback is blocked.");
                long target = (long)Math.Round((packet.Qpc100ns - session.StartQpc100ns) * (48000 / 10_000_000.0));
                if (target < -48000 || target + packet.Frames > 48000L * MaxSeconds) throw new InvalidDataException("Local playback/transcription currently supports stopped recordings up to ten minutes.");
                while (target > writtenFrames)
                {
                    int gap = (int)Math.Min(target - writtenFrames, 48000);
                    pcm.Write(silence, 0, gap * 2); writtenFrames += gap; inserted += gap;
                }
                int skip = (int)Math.Min(Math.Max(writtenFrames - target, 0), packet.Frames);
                trimmed += skip;
                byte[] converted = new byte[(packet.Frames - skip) * 2];
                for (int frame = skip; frame < packet.Frames; frame++)
                {
                    double value = 0;
                    for (int channel = 0; channel < track.Channels; channel++)
                    {
                        float sample = BitConverter.ToSingle(obj.Audio, packet.ByteOffset + frame * track.BlockAlign + channel * 4);
                        if (!float.IsFinite(sample)) throw new InvalidDataException("Non-finite audio sample.");
                        value += sample / track.Channels;
                    }
                    peak = Math.Max(peak, Math.Abs(value)); squares += value * value; sampleCount++;
                    BinaryPrimitives.WriteInt16LittleEndian(converted.AsSpan((frame - skip) * 2, 2), (short)Math.Round(Math.Clamp(value, -1, 1) * short.MaxValue));
                }
                pcm.Write(converted); writtenFrames += packet.Frames - skip;
            }
        }
        if (writtenFrames == 0) throw new InvalidDataException("Track has no saved audio.");
        byte[] wav = new byte[checked(44 + (int)pcm.Length)];
        "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 48000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 96000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), checked((int)pcm.Length));
        pcm.GetBuffer().AsSpan(0, (int)pcm.Length).CopyTo(wav.AsSpan(44));
        return new(wav, writtenFrames / 48000.0, 20 * Math.Log10(Math.Max(peak, 1e-10)),
            20 * Math.Log10(Math.Max(Math.Sqrt(squares / Math.Max(sampleCount, 1)), 1e-10)), inserted, trimmed);
    }

    public static async Task Play(Vault vault, string id, string track, double start, int seconds)
    {
        var rendered = Render(vault, id, track);
        if (start < 0 || start >= rendered.DurationSeconds || seconds is < 1 or > 120) throw new ArgumentException("Select a valid start and 1–120 playback seconds.");
        using var reader = new WaveFileReader(new MemoryStream(rendered.Wav, writable: false));
        reader.CurrentTime = TimeSpan.FromSeconds(start);
        using var player = new WasapiPlayerBuilder().Build();
        player.Init(reader); player.Play();
        Console.WriteLine($"Playing {track} locally from {start:F2}s for up to {seconds}s. No decrypted audio file is written.");
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, rendered.DurationSeconds - start)));
        player.Stop();
        Console.WriteLine("Playback stopped.");
    }
}
