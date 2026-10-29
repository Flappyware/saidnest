# SaidNest Roadmap

Reviewed October 28, 2026. The public preview is deployed; the recording system is planned. The [architecture](ARCHITECTURE.md) and [plan review](PLAN_REVIEW.md) describe the boundaries and gaps behind these milestones.

## Foundation preview

Status: Deployed.

The website and interactive timeline contain fictional sample recordings. Search, transcript selection, copy, and text download work. The Worker health endpoint reports recording, transcription, pairing, and relay as unavailable. Application code is MIT licensed, GitHub CI checks and builds the starter, and production deployment is manual. There is no private-recording ingestion or persistence yet.

## First Windows implementation

Status: In progress; development recorder implemented, local-alpha gates still open.

The [Windows prototype](WINDOWS_PROTOTYPE.md) has a native command-line capture path, encrypted chunk store, passphrase/account key wrappers, source meters, explicit pause/stop controls, inspection, and WAV export. Synthetic storage, corruption, overload, and forced-process-exit checks passed locally. A generated-tone process-loopback smoke test and a selected microphone level probe passed; this does not establish real duplex meeting reliability. An initial English synthetic corpus and pinned Whisper CPU/CUDA smoke benchmark exist. Production model selection and representative streaming/meeting measurements remain open.

Deliver one reliable vertical slice before extending the cloud interface or building every device client:

1. Inventory the first target computer and fix initial language scope and a permitted reference corpus.
2. Compare candidate runtimes on the same inputs, including concurrent microphone/output tracks; retain quality, memory, provisional latency, finalization latency, and real-time-factor results.
3. Decide owner identity, key recovery, secure archive/checkpoint format, process boundaries, and the local worker contract.
4. Persist recoverable Windows microphone/system-output recordings and report interruptions.
5. Connect one local model and retrieve a real timestamped transcript offline.

Gate: recording and saved audio survive an application/repository restart, inference cannot block capture, and the end-to-end result can be retrieved without the cloud. Broader hardware comparisons inform model defaults without blocking the first target.

## Local alpha

Status: Planned; builds on the first Windows slice.

Add the repository API, search, audio-linked playback, human corrections, export, retention, clean-machine installation, offline model provisioning, and verified backup/key restoration. Define update authenticity and rollback before distribution. Keep capture, delivery, and transcription states distinct.

Gate: a recoverable hour-long offline meeting with separate source tracks, measured recognition quality and drift, followed by search and playback from the saved archive. Test device switching, echo, overlap, silence, inference overload, disk pressure, UI close, and reboot behavior. A read-only local MCP milestone can follow this alpha independently of paid services.

## Connected alpha

Status: Planned; depends on local identity, recovery, and repository permissions.

1. Add authenticated device/browser pairing, key verification, revocation, and direct LAN access.
2. Add encrypted remote routing, durable acknowledgments, final manifests, resumable transfer, bounded queues, and protocol-version handling.
3. Enforce deletion tombstones so offline uploads or late worker results cannot restore deleted content.
4. Build native Android microphone capture and automatic private sync on an explicitly supported device/OS matrix.
5. Measure relay cost for active audio, idle connections, and reconnect bursts; validate self-hosted deployment too.

Gate: capture through network interruptions and repository downtime, then deliver all saved audio without duplicates. Deny unpaired/revoked/cross-library access, recover relay coordination after restart, and surface microphone permission loss instead of recording apparent silence. Cloud sign-in, relay, and billing outages must leave local access usable.

## Managed service pilot

Status: Planned; optional services do not gate useful local operation.

Validate demand and unit economics before fixing prices. Add isolated hosted transcription jobs with repository-issued grants, authenticated results, cancellation, measured retention cleanup, and a selected compute provider. Add managed connectivity subscriptions and compute charging with explicit allowances and budget reservations. Choose payment and support processes before taking real payments.

Gate: another customer's jobs/content cannot be accessed, retries do not duplicate charges, concurrent jobs cannot exceed their authorized spending policy, cancelled results cannot reappear, and cleanup succeeds after crashes. Verify subscription expiry, provider failures, duplicate payment events, credits/refunds, and continued local archive access. Begin with an invited pilot.

## MCP and complete MVP release

Status: Planned; local read-only MCP can start after local alpha.

Add scoped listing, search, transcript, and excerpt tools with source/timestamp context and separate audio permissions. Treat transcript text as source material rather than instructions. Extend CI and manual evidence to the native components, repository, protocol, supported platforms, accessibility, and update/migration recovery.

Gate: authorized agents retrieve the intended excerpts; unauthorized clients cannot infer restricted recordings through search. Record release/support ownership, a private vulnerability-reporting route, staging/rollback evidence, and content-free monitoring. Publish supported hardware/OS and operational limits.

## Later

Validate Pixel Watch capture and battery behavior, then expand desktop and mobile platforms. Evaluate diarization, semantic search, and optional summaries after reliable recording and retrieval work. Automatic meeting/calendar integration, ambient capture, multi-writer archive sync, and organization collaboration are outside the initial MVP.
