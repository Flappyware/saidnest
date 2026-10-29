using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SaidNest.Capture;

static class SelfTests
{
    static SessionInfo Session() => new(1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, ChunkPump.QpcNow(), Stopwatch.Frequency, "recording", "not-configured",
        [new("mic", "synthetic-mic", "Synthetic mic", 48000, 1, 32, "ieee-float"), new("output", "synthetic-output", "Synthetic output", 48000, 2, 32, "ieee-float")]);
    static void Assert(bool condition, string message) { if (!condition) throw new Exception("Self-test failed: " + message); }
    static byte[] Audio(int frames, int channels)
    {
        byte[] bytes = new byte[frames * channels * 4];
        for (int i = 0; i < frames * channels; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), (float)(0.01 * Math.Sin(i * 0.05)));
        return bytes;
    }

    public static async Task Run(string parent)
    {
        string folder = Path.Combine(Path.GetFullPath(parent), Guid.NewGuid().ToString("N"));
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var checks = new List<string>();
        using var vault = Vault.Create(folder, password);
        vault.Write("probe.snm", new { value = "synthetic fixture" }, [1, 2, 3, 4]);
        using (var reopened = Vault.Open(folder)) Assert(reopened.Read("probe.snm").Audio.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Windows-account unlock");
        using (var recovered = Vault.OpenRecovery(folder, password)) Assert(recovered.Read("probe.snm").Metadata.GetProperty("value").GetString() == "synthetic fixture", "Portable recovery wrapper");
        bool wrongPassword = false;
        try { using var wrong = Vault.OpenRecovery(folder, password + "wrong"); } catch (CryptographicException) { wrongPassword = true; }
        Assert(wrongPassword, "Wrong recovery passphrase rejected");
        string keyPath = Path.Combine(folder, "vault.json");
        var keyFile = JsonSerializer.Deserialize<KeyFile>(File.ReadAllBytes(keyPath), Vault.Json)!;
        File.WriteAllBytes(keyPath, JsonSerializer.SerializeToUtf8Bytes(keyFile with { ProtectedKey = "AQID" }, Vault.Json));
        using (var restored = Vault.OpenRecovery(folder, password)) restored.RestoreLocalUnlock();
        using (var local = Vault.Open(folder)) Assert(local.Read("probe.snm").Audio.Length == 4, "recovery restores local unlock after loss");
        File.Copy(Path.Combine(folder, "probe.snm"), Path.Combine(folder, "renamed.snm"));
        bool movedRejected = false;
        try { vault.Read("renamed.snm"); } catch (CryptographicException) { movedRejected = true; }
        Assert(movedRejected, "encrypted objects are bound to owner and relative object path");
        checks.Add("Windows-account unlock, recovery wrapper, and wrong-passphrase denial");

        var session = Session();
        var pump = new ChunkPump(vault, session);
        Assert(Archive.Inspect(vault, session.Id).State == "recording", "active writer is not reported as interrupted");
        foreach (var track in session.Tracks) Assert(pump.Offer(track.Id, Audio(48000, track.Channels), 0, session.StartQpc100ns, 0), "accept synthetic source");
        await pump.Finish("synthetic test", true);
        Assert(Archive.Inspect(vault, session.Id) is { State: "complete", VerifiedChunks: 2, VerifiedFrames: 96000 }, "two-source final manifest");
        string wav = folder + ".synthetic-export.wav";
        Archive.Export(vault, session.Id, "mic", wav);
        using (var reader = new NAudio.Wave.WaveFileReader(wav)) Assert(reader.Length == 48000 * 4, "timestamp-aligned WAV export");
        checks.Add("Two-source durable chunks, final-manifest verification, and WAV export");

        string chunkPath = Directory.GetFiles(vault.SessionPath(session.Id), "mic-*.snc").Single();
        var original = File.ReadAllBytes(chunkPath);
        var altered = original.ToArray(); altered[^1] ^= 1; File.WriteAllBytes(chunkPath, altered);
        Assert(Archive.Inspect(vault, session.Id).State == "corrupt", "tampered ciphertext");
        File.WriteAllBytes(chunkPath, original);
        File.Move(chunkPath, chunkPath + ".missing");
        Assert(Archive.Inspect(vault, session.Id).State == "corrupt", "missing finalized chunk");
        File.Move(chunkPath + ".missing", chunkPath);
        checks.Add("Tampered ciphertext and missing finalized chunks fail verification");

        var blocked = new ChunkPump(vault, Session(), capacity: 1);
        Assert(!blocked.Offer("mic", Audio(2, 1), 0, ChunkPump.QpcNow(), 0), "bounded queue rejection");
        Assert((await blocked.Finish("queue test", true)).State == "interrupted", "overflow cannot finalize as complete");
        checks.Add("Queue overload stops acceptance without blocking capture and reports interrupted");

        var childInfo = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) childInfo.ArgumentList.Add(typeof(SelfTests).Assembly.Location);
        childInfo.ArgumentList.Add("crash-fixture"); childInfo.ArgumentList.Add("--vault"); childInfo.ArgumentList.Add(folder);
        using var child = Process.Start(childInfo) ?? throw new Exception("Cannot start crash fixture.");
        string? id;
        try { id = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        Assert(id != null && Guid.TryParseExact(id, "N", out _), "crash fixture committed an encrypted chunk");
        var partial = Archive.Inspect(vault, id!);
        Assert(partial is { State: "interrupted", VerifiedChunks: 1, OrphanPartials: 1 }, "real process kill retains only committed chunks and reports orphan");
        checks.Add("Forced child-process termination: committed audio recovered, uncommitted partial ignored, completion not invented");
        File.WriteAllText(Path.Combine(folder, "self-test-result.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks, privateAudioCaptured = false }, Vault.Json));
        Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, checks }, Vault.Json));
    }

    public static async Task CrashChild(string root)
    {
        using var vault = Vault.Open(root);
        var session = Session();
        Directory.CreateDirectory(vault.SessionPath(session.Id));
        using var lease = new FileStream(Path.Combine(vault.SessionPath(session.Id), "capture.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        vault.Write($"sessions/{session.Id}/session.snm", session);
        var audio = Audio(48000, 1);
        vault.Write($"sessions/{session.Id}/mic-00000000.snc", new ChunkInfo(1, "mic", 0, 0, 48000, [new(0, 48000, 0, session.StartQpc100ns, session.StartQpc100ns, 0)]), audio);
        File.WriteAllBytes(Path.Combine(vault.SessionPath(session.Id), "uncommitted.partial"), new byte[13]);
        Console.WriteLine(session.Id); Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
