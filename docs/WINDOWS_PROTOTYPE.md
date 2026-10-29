# Windows recording prototype

Development prototype, October 28, 2026. The deployed website remains a fictional preview. This local command-line recorder is a separate executable; it is not yet connected to the timeline or transcription worker.

## Start recording locally

On Windows with PowerShell 7 and the .NET 10 SDK, double-click `Start-SaidNest.cmd` in the repository folder, or run:

```powershell
./scripts/windows/start-recorder.ps1
```

Choose the microphone and output device from the displayed names. Exclude a source with `X`. First use asks for a recovery passphrase twice; the program never accepts it in command arguments or logs it. Keep that passphrase separately from a backup of the entire vault, including `vault.json`. The initial restore/build needs network access to fetch pinned packages; later launches use the built executable and require no cloud connection.

Recording starts only after explicit source selection and successful vault setup. The console shows recording/paused state, elapsed time, source levels, missing callbacks, and queued bytes. Press `P` to pause/resume, or `Q`/Ctrl+C to stop. The launcher stops after ten minutes by default; `-Seconds 60` changes its fixed limit. The prototype permits up to one hour but has not yet passed an hour-long qualification test. Closing or killing the process leaves the session interrupted; inspection recovers committed chunks.

All local outputs default to ignored `.saidnest/`. Real recordings must not be committed, uploaded, or used in public screenshots. The microphone default chosen by Windows may be a virtual device, so check the displayed source name.

## Inspect, recover, and export

After building, the executable is `native/SaidNest.Capture/bin/Release/net10.0-windows10.0.19041.0/SaidNest.Capture.exe`. These commands can also be run through the project:

```powershell
dotnet run --project native/SaidNest.Capture -c Release --no-build -- devices
dotnet run --project native/SaidNest.Capture -c Release --no-build -- sessions
dotnet run --project native/SaidNest.Capture -c Release --no-build -- inspect --session SESSION_ID
dotnet run --project native/SaidNest.Capture -c Release --no-build -- export --session SESSION_ID --track mic --out .saidnest/exports/microphone.wav
dotnet run --project native/SaidNest.Capture -c Release --no-build -- restore-unlock
```

Replace `SESSION_ID` with the identifier printed when capture starts. Use track `output` for system audio. Export is an explicit plaintext WAV operation and never overwrites a destination. The encrypted source remains intact. `restore-unlock` asks for the recovery passphrase and rewraps the key for the current Windows account; it does not bypass a missing recovery key. Back up the complete vault before moving it to another computer. Actual cross-machine restoration remains a qualification task.

`devices` only enumerates sources. `probe-mic --mic DEVICE_ID` opens the selected microphone for five seconds and retains level statistics only, without saving audio. Normal `record --mic DEVICE_ID --output DEVICE_ID --seconds 60` requires an already initialized vault. The literal `default` resolves Windows's current default; sources are never changed automatically after capture begins.

## Implementation decisions

- **Prototype stack:** C#/.NET 10 and pinned NAudio.Wasapi 3.3.0. The target environment already has the SDK; this validates native capture without first installing another toolchain. Rust remains a possible later core choice, rather than a settled implementation requirement. Source and package hashes are locked in `packages.lock.json`.
- **Capture:** shared-mode WASAPI, microphone and loopback as independent tracks, requested 48kHz IEEE float32, mono microphone and stereo output, 50ms requested capture buffering. Windows performs supported shared-mode conversion. The request is not a measured end-to-end latency claim.
- **Timing:** preserve device positions, WASAPI QPC positions in 100ns units, arrival times, and buffer flags per packet. The shared session clock maps packets during export and inserts silence for gaps. Timestamp-error packets block aligned export. Clock-drift limits, echo cancellation, device switching, and long-session timestamp fidelity remain unqualified.
- **Persistence:** callbacks reserve memory and copy into a nonblocking queue capped at 8MiB of PCM and 512 packets. A dedicated writer encrypts approximately one audio-second per track per chunk. It flushes data before committing a file name; stopped recording drains queued packets before writing a final manifest. Queue overflow stops acceptance and prevents a complete final state. Model inference is absent from the callback and writer.
- **Encryption and owner:** random 256-bit archive key; AES-256-GCM with a fresh 96-bit nonce, owner/object-path authenticated context, encrypted audio and metadata. Current-user Windows DPAPI protects automatic local unlock. A separate recovery wrapper uses PBKDF2-HMAC-SHA256 with 600,000 iterations and a random salt. New vault directories disable inherited permissions and grant the current Windows user access.
- **Completion:** encrypted manifests identify chunk names, sequence numbers, sample ranges, and ciphertext checksums. Inspection verifies authentication, indexes and final references. An active writer holds an OS file lease; a crashed process releases it. No final manifest means interrupted, not complete. Uncommitted `.partial` files are reported and not silently adopted or deleted. “Complete” means the received chunks were finalized; inspect discontinuity flags too, since this does not establish gap-free capture.

The threat boundary protects files at rest against access without the account/recovery key and detects damaged or moved archive objects. It does not protect an unlocked account from its other processes, a compromised operating system, or an administrator. Managed passphrase strings cannot be reliably erased from the .NET heap. There is no repository service, browser authorization, pairing, key rotation, remote protocol, or independently reviewed archive specification yet. Process-crash recovery tests do not establish power-loss or disk-failure durability. The chunk format is explicitly experimental and must not become a long-term archive promise without migration support.

## Reproduce development evidence

```powershell
./scripts/windows/baseline.ps1
dotnet restore native/SaidNest.Capture --locked-mode
dotnet build native/SaidNest.Capture -c Release --no-restore
dotnet run --project native/SaidNest.Capture -c Release --no-build -- self-test
dotnet run --project native/SaidNest.Capture -c Release --no-build -- smoke-loopback
dotnet run --project native/SaidNest.Capture -c Release --no-build -- corpus
./scripts/windows/provision-whisper.ps1
py -3.13 scripts/windows/benchmark_whisper.py
py -3.13 -m unittest discover -s test -p '*_test.py'
```

`self-test` uses synthetic PCM only. It checks account unlock, passphrase recovery and local-unlock restoration, object-path authentication, two-track persistence/export, damaged or missing chunks, bounded overload, active-versus-interrupted state, and recovery after forcibly killing its own fixture process. It leaves artifacts under ignored test directories.

`smoke-loopback` plays a low-volume generated tone for a four-second test. It captures only its own process audio, never the room microphone or unrelated applications. It validates the common WASAPI/persistence path, but does not qualify default endpoint loopback, real duplex meetings, or user pause/resume behavior. Those require supervised source-specific recordings.

`corpus` generates locally synthesized English speech from project-authored fictional text plus five seconds of silence, reference text, and a SHA-256 manifest. It is a smoke corpus rather than a representative meeting corpus. Voice installation affects the waveform; compare hashes before comparing results across machines.

Inference provisioning downloads the official pinned whisper.cpp `b5454` CUDA 12.4 distribution and `ggml-tiny.en.bin` at a pinned model revision, checking their upstream SHA-256 digests before use. Artifacts stay local and excluded from Git and the website build. This is development model provisioning, not production model selection. Source and model are MIT licensed; the bundled CUDA libraries have their own redistribution requirements. Do not package the tool without preserving upstream notices.

The standard-library Python benchmark runs the same generated speech/silence inputs on CPU and CUDA, then two concurrent CUDA jobs. It records process wall time, engine timing fields, source duration, normalized word error rate, literal silence output versus known non-speech markers, backend evidence, out-of-range segment endpoints, and immutable input identifiers. Each invocation loads a new model. It does not measure streaming response time, finalization latency, peak memory, thermals, or echo/overlap recognition. Two concurrent copies of one synthetic clip are not a representative two-person meeting.

## Next qualification gates

1. Initialize an owner-controlled vault, record the selected real microphone and output together, and verify pause/resume, source selection, playback/export, and interruption feedback.
2. Test a 60-minute meeting, driver/device failures, sleep/resume, disk pressure, queue saturation, and forced shutdown while audio is arriving; measure drift and retained/lost boundaries.
3. Build a permitted, annotated meeting corpus and compare the planned streaming engine with portable Whisper recognition. Select a model only after quality, silence, memory, provisional latency, finalization and concurrent-source measurements.
4. Introduce the versioned local worker contract, consume decrypted audio in memory, validate transcript timestamps and non-speech markers, and connect real saved sessions to an authenticated local timeline. The public preview remains synthetic until that integration exists.

The implementation uses Microsoft's [WASAPI loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording), NAudio's [recorder API](https://github.com/naudio/NAudio/blob/v3.3.0/src/NAudio.Wasapi/WasapiRecorder.cs), and Windows [DPAPI](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata?view=net-10.0). These references document underlying APIs, not a security assessment of this prototype.
