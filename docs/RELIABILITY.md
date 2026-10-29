# Windows reliability pass

Reviewed October 29, 2026. This pass hardens the existing local prototype; it does not establish local-alpha readiness or change cloud-preview capabilities.

## Isolated queue records

Each `job-*.snq` is read, authenticated, deserialized and checked independently. Unsupported versions, invalid identity/state/progress fields, damaged ciphertext and unreadable files become entries in `issues`, while valid jobs remain available. Invalid filenames receive a safe record fingerprint; raw filenames, exception text and executable settings are not included in issue summaries.

Damaged job files stay at their original paths with their bytes unchanged. They are excluded from processing, not deleted or silently rewritten. Archive encryption authenticates the object path, so moving ciphertext to a quarantine folder would itself invalidate that context. Restore a verified backup at its original path with automatic refinement disabled, inspect the queue, and resume only after confirming the intended jobs. A repaired valid record becomes eligible again according to its saved state. Audio, saved versions, healthy jobs and recording-list responses remain independent of an isolated job record.

If a record becomes unreadable during decoding, the current window is cancelled, the separate display status becomes isolated rather than remaining falsely running, and the next healthy job can proceed. The damaged record itself is still preserved.

This isolation covers queue job records. Broader per-record isolation for damaged transcript versions or other derived archive metadata, policy recovery, storage quotas and checkpoint cleanup remain follow-up work.

## Worker health

The worker stores encrypted, content-free telemetry in `refinement-queue/worker-health.snq`: instance/process identity, heartbeat, activity, current job ID, last successful progress, isolated-record/failed-job counts, consecutive cycle failures and safe error codes. No transcript text, audio, raw exception messages or access keys are included. Last progress and the last error survive worker-instance replacement.

An independent heartbeat publishes about every two seconds, including while the decoder is running or the loop is waiting after an error. The authenticated `/api/local/health` response separates web-service readiness from `workerHealthy`; HTTP 200 alone is not worker health. The queue response includes `issues` and worker health. The local timeline polls health separately from audio/catalog work and shows status without replacing the transcript or reference editor. The CLI `worker-health` and `refinement-queue` commands provide the same local diagnostics.

Health states distinguish healthy work/waiting, degraded queues, fault/retry backoff, an unresponsive heartbeat (over fifteen seconds), a stalled active cycle (over three minutes), a stopped worker and unavailable telemetry. These are diagnostic thresholds, not promises of completion time. PID plus process start time prevents an old heartbeat being mistaken for a new process. Damaged health metadata reports unavailable rather than taking down archive access. A lifetime host lease permits one health-publishing worker per vault; the existing per-window inference/worker leases remain in force.

A second worker waits without overwriting the active owner's heartbeat, then takes over when that owner exits. This covers starting the timeline service while a standalone refinement worker is already running.

Recoverable cycle errors report a fault code and retry after a delay. A failed individual model job remains failed and does not stop healthy jobs. Health reporting does not add an independent Windows watchdog for the supervisor itself, nor automatically kill a stalled cycle. Those remain explicit operational gaps.

## Decoder lifetime after a crash

Each local decoder is assigned to an unnamed, non-inheritable Windows Job Object configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, before the parent sends audio. Normal cancellation still stops the process tree and waits for exit. If the parent crashes, Windows closes its job handle and terminates contained decoder descendants. Failure to establish the lifetime boundary stops the child and fails the job rather than continuing without protection. See [Microsoft Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).

The ordinary integration suite starts a long-running child/grandchild fixture, waits until both are contained, kills **only** their parent, and verifies both exit without helping with a tree kill. A local GPU test additionally waits for the real provisioned decoder to report CUDA allocation, kills only its parent, and verifies decoder exit. That test uses generated tone in memory, never private speech:

```powershell
dotnet run --project native/SaidNest.Capture -c Release --no-build -- decoder-crash-test --install .saidnest/inference/install-large-v3.json
```

This establishes active-decoder crash cleanup on the tested Windows runtime. Process start and job assignment are separate operations; atomic suspended creation/assignment is not claimed. Future decoder integrations that perform work before input must account for that interval. Driver/GPU hangs and instantaneous VRAM reclamation are not established by observing process exit.

## Remaining acceptance work

- A full sixty-minute headphone meeting through capture, tiny live text, automatic enqueue, genuine idle refinement, playback and checked references.
- A real Windows sign-in, clean-machine archive/key restore, and validation on the 4070/5090 laptops and M1 inference path.
- Backlog age/limits, owner-visible run scheduling, system RAM/disk-pressure handling, retention/compression and safe checkpoint cleanup.
- Persistent transcript corrections distinct from evaluation references, larger representative accuracy evidence, word-boundary/silence/echo tests, and indexed retrieval beyond the current catalog window.
- Broader archive metadata isolation and independent recovery if the background supervisor itself exits.
