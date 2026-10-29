# Windows recording prototype

Development prototype, October 28, 2026. The deployed website remains a fictional preview. The separate Windows executable now connects saved recordings to experimental local transcription and an authenticated local timeline.

## Start recording locally

On Windows with PowerShell 7 and the .NET 10 SDK, double-click `Start-SaidNest.cmd` in the repository folder, or run:

```powershell
./scripts/windows/start-recorder.ps1
```

Choose the microphone and output device from the displayed names. Exclude a source with `X`. First use asks for a recovery passphrase twice; the program never accepts it in command arguments or logs it. Keep that passphrase separately from a backup of the entire vault, including `vault.json`. The initial restore/build needs network access to fetch pinned packages; later launches use the built executable and require no cloud connection.

Recording starts only after explicit source selection and successful vault setup. The console shows recording/paused state, elapsed time, source levels, missing callbacks, and queued bytes. Press `P` to pause/resume, or `Q`/Ctrl+C to stop. The launcher stops after ten minutes by default; `-Seconds 60` changes its fixed limit. The prototype permits up to one hour but has not yet passed an hour-long qualification test. Closing or killing the process leaves the session interrupted; inspection recovers committed chunks.

All local outputs default to ignored `.saidnest/`. Real recordings must not be committed, uploaded, or used in public screenshots. The microphone default chosen by Windows may be a virtual device, so check the displayed source name.

## Open the local timeline

After recording, double-click `Start-Local-SaidNest.cmd`, or run `./scripts/windows/start-local.ps1`. Keep its window open; Ctrl+C stops the service. It opens a private browser tab at `http://127.0.0.1:8788/app/`. Opening that address manually in another tab does not grant access; the launcher supplies a per-launch key. If the service is already running, use its existing tab rather than starting a second copy on the same port.

During development the agent may open the same service in the background. `Stop-Local-SaidNest.cmd` stops that tracked process after checking its executable and start time; it does not stop capture or delete recordings. For foreground launches, use Ctrl+C in the launch window. The prototype catalog loads the 100 most recently updated recordings; search is limited to records loaded in this tab.

Select a recording and source, play saved audio, or choose **Transcribe locally**. Search scans transcript text loaded into this tab. Timestamp buttons seek the selected source; copy and text download include recording and timestamp context. **Refresh recordings** reloads the archive after a new capture stops. Transcription runs after capture, independently of its writer; it is not live meeting transcription. The experimental `tiny.en` model is English-only and this worker requires CUDA. Provision it first using `./scripts/windows/provision-whisper.ps1`; missing dependencies fail locally without switching processing destination.

The adapter decrypts each source to an in-memory 48kHz mono PCM16 WAV, downmixing stereo for playback/inference. Original float32 source tracks remain unchanged. It inserts silence at saved clock gaps, including manual pauses, so passage times remain relative to recording start. Playback and inference currently accept stopped recordings up to **ten minutes**, while recording itself permits one hour. Playback supports intact interrupted sessions; inference requires a verified final manifest. Invalid hardware timestamps block aligned playback. Longer sessions need a bounded streaming adapter before local-alpha qualification.

Inference sends WAV bytes through the pinned CLI's standard input and reads segment output in memory; it writes no decrypted intermediate WAV or transcript JSON. Validated segments are stored as new immutable encrypted `.snt` revisions with model/runtime provenance. Known non-speech markers and invalid segment ranges are discarded; this is not a speech detector or a recognition-quality guarantee. An explicit low-signal threshold can skip inference while retaining audio; it is not proof that no speech occurred. The interface shows the latest revision; revision browsing and corrections remain planned.

The service binds only `127.0.0.1`, validates Host and Origin, and requires a fresh random bearer key for every private API/audio request. The launch fragment is removed from the visible address and retained in per-tab session storage. Responses use `no-store`, same-origin policies, and no external scripts. Browser playback uses an in-memory Blob. The bootstrap URL is saved only as an encrypted vault object; console output omits credentials. Stop/restart invalidates the previous key. This HTTP loopback service is for the same account and computer, not LAN or remote access; it does not defend against another process controlling the unlocked account. Copies and downloads create plaintext at the user's request.

Useful commands after building:

```powershell
dotnet run --project native/SaidNest.Capture -c Release --no-build -- diagnose --session SESSION_ID
dotnet run --project native/SaidNest.Capture -c Release --no-build -- play --session SESSION_ID --track mic --start 20 --seconds 9
dotnet run --project native/SaidNest.Capture -c Release --no-build -- transcribe --session SESSION_ID
dotnet run --project native/SaidNest.Capture -c Release --no-build -- serve
```

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

The threat boundary protects files at rest against access without the account/recovery key and detects damaged or moved archive objects. It does not protect an unlocked account from its other processes, a compromised operating system, or an administrator. Managed passphrase strings cannot be reliably erased from the .NET heap. There is no paired-device identity, key rotation, remote protocol, or independently reviewed archive specification yet. Process-crash recovery tests do not establish power-loss or disk-failure durability. The chunk format is explicitly experimental and must not become a long-term archive promise without migration support.

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

1. Extend short owner-recording evidence to representative two-source speech and interruption feedback. A short local capture, pause/resume, playback, and encrypted transcript/timeline path now work; system output speech quality is not established.
2. Test a 60-minute meeting, driver/device failures, sleep/resume, disk pressure, queue saturation, and forced shutdown while audio is arriving; measure drift and retained/lost boundaries.
3. Build a permitted, annotated meeting corpus and compare the planned streaming engine with portable Whisper recognition. Select a model only after quality, silence, memory, provisional latency, finalization and concurrent-source measurements.
4. Replace the pinned post-capture CLI adapter with a versioned worker contract and bounded long-session/streaming processing. Add revision browsing, corrections, indexed search, installation, and backup restoration. The cloud preview remains synthetic; real archives are accessed through the local service.

The implementation uses Microsoft's [WASAPI loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording), NAudio's [recorder API](https://github.com/naudio/NAudio/blob/v3.3.0/src/NAudio.Wasapi/WasapiRecorder.cs), and Windows [DPAPI](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata?view=net-10.0). These references document underlying APIs, not a security assessment of this prototype.


The `integration-test` command uses synthetic PCM and fictional text to verify pause-aligned WAV output, discontinuity correlation, timestamp bounds, API/audio authorization, cross-origin denial, Host validation, no-store responses, and denial of static vault files. It runs in Windows CI. `serve-fixture` opens a synthetic archive for UI checks; keep private recordings out of browser automation and screenshots.
