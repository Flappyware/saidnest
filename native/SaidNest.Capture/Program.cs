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
            if (command == "output-activity") { Console.WriteLine(JsonSerializer.Serialize(await Recorder.OutputActivity(), Vault.Json)); return 0; }
            if (command == "integration-test") { await IntegrationTests.Run(Option("--out") ?? ".saidnest/integration-tests"); return 0; }
            if (command == "serve-fixture")
            {
                string fixtureRoot = Path.Combine(Option("--out") ?? ".saidnest/ui-fixtures", Guid.NewGuid().ToString("N"));
                using var fixtureVault = Vault.Create(fixtureRoot, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                string fixtureId = await IntegrationTests.Fixture(fixtureVault);
                using var fixtureLease = args.Contains("--live") ? new FileStream(Path.Combine(fixtureVault.SessionPath(fixtureId),"capture.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None) : null;
                if (fixtureLease!=null)
                {
                    var transcript=Transcription.Latest(fixtureVault,fixtureId)!;
                    foreach(var track in transcript.Tracks) fixtureVault.Write($"sessions/{fixtureId}/live-{track.Track}-00000000-draft.snt",new LivePart(1,track.Track,0,false,0,4,DateTimeOffset.UtcNow,track));
                    fixtureVault.Replace($"sessions/{fixtureId}/live-status.snl",new LiveStatus(1,fixtureId,"running",DateTimeOffset.UtcNow,"Synthetic live fixture. Draft text demonstrates the interface; no speech is recognized.",[new("mic",0,4,0,2,-40,-43,.04),new("output",0,4,0,0,-200,-200,.01)],"fixture","fixture","fixture"));
                    _=Task.Run(async()=> {
                        while(!File.Exists(Path.Combine(fixtureVault.SessionPath(fixtureId),"stop-request.sne"))) await Task.Delay(100);
                        fixtureLease.Dispose();
                        var previous=LiveTranscription.ReadStatus(fixtureVault,fixtureId)!;
                        fixtureVault.Replace($"sessions/{fixtureId}/live-status.snl",previous with {State="complete",UpdatedUtc=DateTimeOffset.UtcNow,Notice="Synthetic fixture stopped. No microphone was opened.",Tracks=previous.Tracks.Select(t=>t with {FinalizedSeconds=4,FinalPassages=t.Track=="mic"?2:0,ProvisionalPassages=0}).ToArray()});
                    });
                }
                await LocalServer.Run(fixtureVault, Path.GetFullPath("public"), Path.GetFullPath(".saidnest/inference/install.json"), int.Parse(Option("--port") ?? "8789"), true, fixture: true);
                return 0;
            }
            if (command == "serve")
            {
                using var vault = Vault.Open(root);
                await LocalServer.Run(vault, Path.GetFullPath(Option("--public") ?? "public"), Path.GetFullPath(Option("--install") ?? ".saidnest/inference/install.json"),
                    int.Parse(Option("--port") ?? "8788"), !args.Contains("--no-open"));
                return 0;
            }
            if (command == "live-status")
            {
                using var vault = Vault.Open(root);
                Console.WriteLine(JsonSerializer.Serialize(LiveTranscription.ReadStatus(vault, Option("--session") ?? throw new ArgumentException("Specify --session ID.")), Vault.Json));
                return 0;
            }
            if (command == "transcript-check")
            {
                using var vault=Vault.Open(root);
                string id=Option("--session") ?? throw new ArgumentException("Specify --session ID.");
                var transcript=Transcription.Latest(vault,id) ?? throw new InvalidDataException("No saved transcript.");
                var results=transcript.Tracks.Select(track=>new {track.Track,track.State,segments=track.Segments.Length,track.DurationSeconds,
                    invalidSegments=track.Segments.Count(s=>!double.IsFinite(s.Start)||!double.IsFinite(s.End)||s.Start<0||s.End<=s.Start||s.End>track.DurationSeconds+.25||string.IsNullOrWhiteSpace(s.Text)),
                    overlappingSegments=track.Segments.Zip(track.Segments.Skip(1)).Count(pair=>pair.Second.Start+.05<pair.First.End)}).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new {transcript.SessionId,revision=transcript.Id,transcript.Runtime,tracks=results},Vault.Json));
                return results.Any(t=>t.invalidSegments>0)?2:0;
            }
            if (command == "stop-recording")
            {
                using var vault = Vault.Open(root);
                string id = Option("--session") ?? throw new ArgumentException("Specify --session ID.");
                if (!Archive.IsRecording(vault,id)) throw new InvalidOperationException("This recording is not active.");
                if (!File.Exists(Path.Combine(vault.SessionPath(id),"stop-request.sne"))) vault.Write($"sessions/{id}/stop-request.sne", new { kind="stop-request", qpc100ns=ChunkPump.QpcNow() });
                Console.WriteLine("Recording stop requested. Committed audio and transcript history are retained.");
                return 0;
            }
            if (command == "open")
            {
                using var vault = Vault.Open(root);
                var launch = Directory.GetFiles(vault.Root,"ui-*.snu").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? throw new IOException("Start the local timeline service first.");
                string url = vault.Read(Path.GetFileName(launch)).Metadata.GetProperty("url").GetString()!;
                var address = new Uri(url);
                if(address.Scheme!="http" || address.Host!="127.0.0.1" || address.AbsolutePath!="/app/") throw new InvalidDataException("Invalid local launch address.");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute=true });
                Console.WriteLine("The private local timeline was reopened. No access key is printed.");
                return 0;
            }
            if (command == "transcribe")
            {
                using var vault = Vault.Open(root);
                var revision = await Transcription.Run(vault, Option("--session") ?? throw new ArgumentException("Specify --session ID."), Option("--install") ?? ".saidnest/inference/install.json");
                Console.WriteLine(JsonSerializer.Serialize(new { revision.SessionId, revision.Id, tracks = revision.Tracks.Select(track => new { track.Track, track.State, segments = track.Segments.Length, track.Notice }) }, Vault.Json));
                return 0;
            }
            if (command == "diagnose")
            {
                using var vault = Vault.Open(root);
                Console.WriteLine(JsonSerializer.Serialize(Diagnostics.Analyze(vault, Option("--session") ?? throw new ArgumentException("Specify --session ID.")), Vault.Json));
                return 0;
            }
            if (command == "play")
            {
                using var vault = Vault.Open(root);
                await TimelineAudio.Play(vault, Option("--session") ?? throw new ArgumentException("Specify --session ID."),
                    Option("--track") ?? "mic", double.Parse(Option("--start") ?? "0", System.Globalization.CultureInfo.InvariantCulture), int.Parse(Option("--seconds") ?? "10"));
                return 0;
            }
            if (command == "probe-mic") { Console.WriteLine(JsonSerializer.Serialize(await Recorder.Probe(Option("--mic") ?? throw new ArgumentException("Select --mic ID or default.")), Vault.Json)); return 0; }
            if (command == "self-test") { await SelfTests.Run(Option("--out") ?? ".saidnest/tests"); return 0; }
            if (command == "corpus") { Corpus.Generate(Option("--out") ?? ".saidnest/corpus/en-synthetic-v0"); return 0; }
            if (command == "crash-fixture") { await SelfTests.CrashChild(root); return 0; }
            if (command == "init")
            {
                Console.WriteLine("Create a local encrypted vault. Keep a backup of vault.json and the archive, plus your recovery passphrase separately.");
                string password;
                while (true)
                {
                    password = Password("Recovery passphrase (12+ characters; Esc cancels): ");
                    if (password.Length < 12) { Console.WriteLine("Use at least 12 characters. Please try again."); continue; }
                    if (password == Password("Confirm passphrase: ")) break;
                    Console.WriteLine("The two entries differed. Nothing was saved or recorded. Please try again.");
                }
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
                var result = await Recorder.Record(vault, mic, output, seconds, liveInstall: args.Contains("--live") ? Option("--install") ?? ".saidnest/inference/install.json" : null);
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
                  record --mic default --output default [--live] [--seconds 600] [--vault PATH]
                  sessions [--vault PATH]          Verify saved sessions; missing final manifests show interrupted
                  inspect --session ID [--vault PATH]
                  diagnose --session ID            Check levels, flags, and intentional pause gaps
                  play --session ID --track mic|output [--start 0] [--seconds 10]
                  transcribe --session ID           Save an encrypted local English transcript revision
                  serve [--port 8788]               Open the authenticated loopback-only timeline
                  open                             Reopen the running private timeline without printing its key
                  live-status --session ID          Show counts and timing only; no transcript text
                  stop-recording --session ID       Request a safe stop for that active recording
                  output-activity                   Read output levels and Discord routing; no audio saved
                  export --session ID --track mic|output --out FILE.wav [--vault PATH]
                  restore-unlock [--vault PATH]    Restore local unlock using recovery passphrase
                  corpus [--out NEW_DIRECTORY]    Generate English synthetic development audio
                  self-test [--out DIRECTORY]     Crypto, recovery, corruption, and process-crash checks
                  integration-test                Synthetic timeline, parser, and local API checks
                  smoke-loopback [--out DIRECTORY]  Four-second generated-tone capture, no microphone
                Sources must be explicitly selected. Omit --mic or --output to exclude that source.
                Local transcription requires the separately provisioned experimental CUDA model.
                Live transcription uses bounded eight-second windows; updates at about four seconds.
                Saved playback uses one-minute windows. Whole-recording retranscription is capped at ten minutes.
                No cloud is required.
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
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("Vault setup cancelled; recording has not started.");
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) { value.Length--; Console.Write("\b \b"); } }
            else if (!char.IsControl(key.KeyChar)) { value.Append(key.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return value.ToString();
    }
}
