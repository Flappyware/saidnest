# SaidNest Project Plan Review

Reviewed October 28, 2026. The architecture direction remains local-first recording and an owner-controlled repository, with optional managed connectivity and compute. The main delivery risk is treating the deployed interface as progress on capture, storage, identity, or inference before those components have executable evidence.

## Current evidence

The [Worker](../src/worker.js) exposes preview health, rejects unsupported methods, and serves static assets. The deployed [timeline](../public/app.js) uses fictional in-memory conversations. The [six behavior tests](../test/worker.test.js) check the starter. The separate [Windows development prototype](WINDOWS_PROTOTYPE.md) implements native capture, encrypted audio and transcript persistence, account/recovery key wrappers, forced-process-exit checks, experimental English CUDA inference, and authenticated loopback playback/search. This saved-recording integration currently supports ten minutes; it is not streaming transcription or a qualified local alpha. Pairing, remote product authentication, relay, and billing remain unimplemented. CI includes native archive, local API, pause-alignment, parser, and benchmark-metric checks; it does not deploy production automatically.

These observations support the foundation preview and early Windows development evidence. They do not establish local alpha readiness, a secure product repository, or real meeting performance. The full internal plan and deployment-specific records stay in the private operations repository; this review contains public product requirements.

## Gaps and plan changes

The following planning changes are recorded. Their associated implementation and validation remain open.

| Gap | Consequence | Added requirement and gate |
| --- | --- | --- |
| G01 Scope and status | A preview can be mistaken for a recording product, while a paid launch can delay basic usefulness | Separate foundation, first Windows slice, local alpha, connected alpha, and paid services; require executable evidence for each |
| G02 Identity and keys | Real archives could exist before recovery, revocation, or key custody are defined | Decide these in the initial baseline; establish a minimal secure archive before private capture |
| G03 Completion states | A transcript or relay receipt can appear to mean all audio is saved | Track capture/delivery/inference independently; require crash-safe acknowledgment and final manifests |
| G04 Offline deletion | A queued upload or late result can recreate deleted content | Add tombstones and cancelled-job generations; test stale uploads/results after reconnect |
| G05 Performance evidence | Single-file speed or a fast first word can hide poor live meeting behavior | Measure both concurrent source tracks, finalization, quality, drift, silence, echo, resource use, and CPU fallback |
| G06 Access boundary | Service login or subscription might be confused with content authorization | One authoritative repository per library; separate entitlements from content grants; deny cross-library and cross-customer access |
| G07 Install and recover | A source checkout cannot establish offline independence for ordinary users | Add installer, offline model provisioning, trusted updates, clean-machine restore, and LAN setup |
| G08 Relay economics | Message frequency, active execution, and reconnects can invalidate allowances | Measure active/idle traffic, quotas, bounded queues, lifecycle recovery, and costs before pricing |
| G09 Hosted-job lifecycle | Concurrent jobs, cancellations, and retries can overrun budgets or retain audio | Reserve spending, isolate inputs/results, make usage idempotent, and verify bounded cleanup before charges |
| G10 Capture experience | Wrong sources or permission loss can create silent or unintended recordings | Require explicit recording, source meters, indicators, accessible controls, and visible interruption states |
| G11 Release readiness | Web-only CI cannot verify native recording, migrations, or updates | Expand platform/protocol checks and add staging, rollback, security review, support, and incident ownership |
| G12 Documentation consistency | Outdated provisional decisions can contradict the release | Record MIT application licensing, manual deployment, one canonical private plan, and synchronized public summaries |

## Decisions to make before implementation

The first baseline must settle initial languages, reference audio and release thresholds, a model/runtime, archive/checkpoint format, owner identity and recovery, and native process/packaging boundaries. Record each decision with its rationale and failure cases. Hardware requirements and delivery dates remain provisional until measurements exist.

LAN certificates, browser/device grants, queue limits, and protocol upgrades must be settled before connected alpha. Hosted compute/payment providers, retention durations, unit economics, allowances, and support responsibilities must be settled before paid service.

The MVP supports manual recording, one owner, one authoritative repository per library, and multiple paired devices. Named remote-speaker recognition, continuous ambient capture, calendar integration, multi-writer replication, organization collaboration, and on-watch inference remain deferred.

## First acceptance target

On one Windows machine, select microphone and system output, persist recoverable audio, run one benchmarked local model, and retrieve a timestamped transcript offline. Demonstrate recovery after a forced application/repository restart. Follow with hour-long meeting tests and the repository timeline. Expand other hardware and remote delivery after that path works.

## Reference constraints

- [Cloudflare WebSockets](https://developers.cloudflare.com/durable-objects/best-practices/websockets/) informs relay lifecycle/reconstruction and idle-versus-active testing.
- [Android foreground service types](https://developer.android.com/develop/background-work/services/fgs/service-types) informs the supported capture and permission matrix.
- [MCP security guidance](https://modelcontextprotocol.io/specification/latest/basic/security_best_practices) informs authorization and isolation of agent retrieval.

These references inform the design; their presence does not constitute an implementation security assessment.
