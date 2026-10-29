using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SaidNest.Capture;

static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This capture prototype requires Windows.");
            string command = args.FirstOrDefault() ?? "help";
            string? Option(string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
            string root = Path.GetFullPath(Option("--vault") ?? ".saidnest/vault");
            if (command == "devices") { Console.WriteLine(JsonSerializer.Serialize(Recorder.Devices(), Vault.Json)); return 0; }
            if (command == "probe-mic") { Console.WriteLine(JsonSerializer.Serialize(await Recorder.Probe(Option("--mic") ?? throw new ArgumentException("Select --mic ID or default.")), Vault.Json)); return 0; }
            if (command == "self-test") { await SelfTests.Run(Option("--out") ?? ".saidnest/tests"); return 0; }
            if (command == "corpus") { Corpus.Generate(Option("--out") ?? ".saidnest/corpus/en-synthetic-v0"); return 0; }
            if (command == "crash-fixture") { await SelfTests.CrashChild(root); return 0; }
            if (command == "init")
            {
                Console.WriteLine("Create a local encrypted vault. Keep a backup of vault.json and the archive, plus your recovery passphrase separately.");
                string password = Password("Recovery passphrase (12+ characters): ");
                if (password != Password("Confirm passphrase: ")) throw new ArgumentException("Passphrases do not match.");
                using var created = Vault.Create(root, password);
                Console.WriteLine("Vault created. Automatic local unlock is restricted to this Windows account; recovery works with your passphrase.");
                return 0;
            }
            if (command == "restore-unlock")
            {
                using var recovery = Vault.OpenRecovery(root, Password("Recovery passphrase: "));
                recovery.RestoreLocalUnlock();
                Console.WriteLine("Local unlock restored for this Windows account.");
                return 0;
            }
            if (command == "smoke-loopback")
            {
                string testRoot = Path.Combine(Option("--out") ?? ".saidnest/loopback-tests", Guid.NewGuid().ToString("N"));
                using var testVault = Vault.Create(testRoot, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                // This records only audio rendered by this test process, never the room microphone.
                var result = await Recorder.Record(testVault, null, null, 4, testProcess: true);
                File.WriteAllText(Path.Combine(testRoot, "test-result.json"), JsonSerializer.Serialize(result, Vault.Json));
                return result.State == "complete" ? 0 : 2;
            }
            if (command == "record")
            {
                int seconds = int.Parse(Option("--seconds") ?? "600", System.Globalization.CultureInfo.InvariantCulture);
                string? mic = Option("--mic"), output = Option("--output");
                using var vault = Vault.Open(root);
                var result = await Recorder.Record(vault, mic, output, seconds);
                return result.State == "complete" ? 0 : 2;
            }
            if (command is "inspect" or "sessions" or "export")
            {
                using var vault = Vault.Open(root);
                if (command == "sessions")
                {
                    foreach (string directory in Directory.EnumerateDirectories(Path.Combine(root, "sessions")))
                        Console.WriteLine(JsonSerializer.Serialize(Archive.Inspect(vault, Path.GetFileName(directory)), Vault.Json));
                    return 0;
                }
                string id = Option("--session") ?? throw new ArgumentException("Specify --session ID.");
                if (command == "export")
                {
                    Archive.Export(vault, id, Option("--track") ?? throw new ArgumentException("Specify --track mic or output."),
                        Option("--out") ?? throw new ArgumentException("Specify an explicit plaintext WAV destination with --out."));
                    Console.WriteLine("Decrypted WAV exported to the requested path. The encrypted archive is unchanged.");
                    return 0;
                }
                var check = Archive.Inspect(vault, id);
                Console.WriteLine(JsonSerializer.Serialize(check, Vault.Json));
                return check.State == "corrupt" ? 2 : 0;
            }
            Console.WriteLine("""
                SaidNest Windows recording prototype
                  devices                         List sources; does not record
                  probe-mic --mic ID              Five-second input-level check; audio not saved
                  init [--vault PATH]             Create encrypted vault and recovery material
                  record --mic default --output default [--seconds 600] [--vault PATH]
                  sessions [--vault PATH]          Verify saved sessions; missing final manifests show interrupted
                  inspect --session ID [--vault PATH]
                  export --session ID --track mic|output --out FILE.wav [--vault PATH]
                  restore-unlock [--vault PATH]    Restore local unlock using recovery passphrase
                  corpus [--out NEW_DIRECTORY]    Generate English synthetic development audio
                  self-test [--out DIRECTORY]     Crypto, recovery, corruption, and process-crash checks
                  smoke-loopback [--out DIRECTORY]  Four-second generated-tone capture, no microphone
                Sources must be explicitly selected. Omit --mic or --output to exclude that source.
                No cloud, transcription model, automatic source switching, or background recording.
                """);
            return command == "help" ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine($"SaidNest: {ex.Message}"); return 1; }
    }

    static string Password(string prompt)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Recovery passphrases require an interactive terminal, not command arguments or redirected input.");
        Console.Write(prompt);
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.WriteLine();
        return value.ToString();
    }
}
