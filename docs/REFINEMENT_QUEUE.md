# Opportunistic local refinement

Implemented October 28, 2026 in the Windows prototype. Live recognition stays on the lightest useful profile (`tiny.en` by default). Refinement is separate, resumable local work; no cloud or account is required.

## Normal use

New successful recordings are automatically queued with the policy's high-quality model (`large-v3` by default), provided that profile has been provisioned. Missing model settings leave the recording saved and report that it was not queued. The recorder does not wait for refinement. Existing recordings are not automatically backfilled; choose **Queue refinement** in the local timeline for one you want processed.

Keep the local timeline service running, or run `refinement-worker` without the browser. Closing a browser tab does not stop the service. Stopping the service or restarting Windows leaves the queue and completed windows on disk; reopen the service/worker to resume. This is not a Windows startup service yet.

The initial policy requires:

- At least 120 seconds since keyboard/mouse input in the worker's Windows session.
- CPU utilization at or below 25% before starting a window.
- AC power, unless battery work is explicitly allowed.
- No active recording or foreground transcription request, and an available inference lock.

Unknown input/power/CPU readings defer work. CPU headroom is an admission check; the worker does not cancel itself because its own decode raises CPU use. It checks input, power, policy, pause/cancel requests, and foreground activity approximately every 200ms during a window. A new input or foreground request cancels the current child decoder, releases the model lock after cancellation/child exit, and leaves that unfinished window for later. Detection interval is not a guaranteed release latency. Archive preparation also checks cancellation between chunks. Completed windows remain encrypted.

Input idle time is session-specific, not a measure of attention or activity in every Windows login. GPU load from unrelated applications, memory pressure, scheduled time windows, and a resident model worker are not yet admission signals. The current CLI decoder exits after each window, releasing its GPU allocation. Qualify busy-GPU behavior and longer workloads before claiming general resource isolation.

## Controls and preferences

The local recording view shows the model, state, processed source seconds, and deferral reason. **Pause refinement**, **Resume when idle**, and **Cancel refinement** persist across service restarts. Cancel retains original audio, prior transcripts, and encrypted checkpoints; it is not deletion. New completion becomes selected only if the owner has not changed the selected version since enqueueing.

Use the built executable, or prefix these examples with `dotnet run --project native/SaidNest.Capture -c Release --no-build --`:

```powershell
refinement-queue
queue-refinement --session SESSION_ID --model large-v3
refinement-queue --job JOB_ID --action pause
refinement-queue --job JOB_ID --action resume
refinement-policy --idle-seconds 120 --max-cpu 0.25 --require-ac
refinement-policy --disable
refinement-policy --enable --auto-queue
refinement-worker
```

`--allow-battery` changes the AC preference; `--no-auto-queue` disables enqueueing future recordings. `--model NAME` sets the future-recording refinement profile. Disabling automatic processing defers jobs without removing them. The launcher accepts `-RefineModel NAME` to queue a chosen model and `-NoRefinement` to skip a recording. The native development-only `--refine-install` path still explicitly requests an immediate foreground pass.

## Durability and privacy

Policy, job state, model settings, parent/source provenance, and per-window text live in authenticated encrypted vault objects under `refinement-queue/`. Each source uses thirty-second windows and an independently committed checkpoint. Job progress can be reconstructed from these checkpoints after an interrupted write; an unfinished window is retried. Atomic object replacement and OS-held locks prevent duplicate worker execution and concurrent queue mutations. Idempotent enqueue avoids creating another job for the same recording, manifest, and model settings unless the earlier job was cancelled.

An enqueued job pins its model/runtime/settings snapshot and final audio manifest. Resuming verifies the model checksum and source; changed or missing dependencies fail the job rather than mixing models. Retry uses **Resume when idle** after repair. Original audio and transcript revisions remain intact on failure. Final revision identity is fixed per job, preventing duplicate revisions after interruption around finalization.

Queue metadata and controls use the same loopback bearer authorization, Host/Origin validation, and no-store policy as the local archive. API profile selection accepts known local model names, not client-supplied executable paths. CLI summaries contain counts/status, not transcript words.

Windows signal references: [GetLastInputInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo), [GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes), and [GetSystemPowerStatus](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus).

## Verification

Generated-audio integration tests exercise active-input/CPU/battery/unknown-status deferral, cancellation for returning input, new capture/live preemption with preserved frames, stale-running recovery from encrypted checkpoints, pause/resume/cancel, singleton execution, idempotent enqueue/completion, failure isolation, version selection, and queue API authorization/cross-origin denial. `queue-cuda-smoke` separately exercises real provisioned large-v3 inference and revision assembly with generated tone/silence and controlled eligibility inputs. It is not a recognition-quality or real-idle benchmark.
