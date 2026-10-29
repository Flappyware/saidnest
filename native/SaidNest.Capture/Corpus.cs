using System.Security.Cryptography;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.Json;
using NAudio.Wave;

namespace SaidNest.Capture;

static class Corpus
{
    public const string Reference = "Ari and Morgan discussed the next version of SaidNest. Keep the microphone and system audio on separate tracks. Recording must continue when transcription slows down. The review is on Tuesday at ten thirty. We need an encrypted archive that can recover after a restart.";

    public static void Generate(string folder)
    {
        if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("Use a new corpus directory; existing audio is never overwritten.");
        Directory.CreateDirectory(folder);
        using var speech = new SpeechSynthesizer();
        speech.SetOutputToWaveFile(Path.Combine(folder, "speech.wav"), new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        speech.Speak(Reference);
        speech.SetOutputToNull();
        File.WriteAllText(Path.Combine(folder, "speech.reference.txt"), Reference);
        using (var writer = new WaveFileWriter(Path.Combine(folder, "silence.wav"), new WaveFormat(16000, 16, 1))) writer.Write(new byte[16000 * 2 * 5], 0, 16000 * 2 * 5);
        File.WriteAllText(Path.Combine(folder, "silence.reference.txt"), "");
        var files = Directory.GetFiles(folder).Order().Select(path => new { file = Path.GetFileName(path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new { version = 1, corpus = "en-synthetic-v0", language = "en", voice = speech.Voice.Name,
            createdUtc = DateTimeOffset.UtcNow, origin = "Local Windows speech synthesis from project-authored fictional text; not private audio.",
            limitation = "Development smoke corpus only. Not a representative meeting-quality or accent benchmark; duplex/echo/noise/human references remain required.", files }, Vault.Json));
        Console.WriteLine("Generated local English speech, matching reference text, silence, and SHA-256 manifest.");
    }
}
