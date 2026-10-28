# SaidNest Roadmap

## Foundation

The first deployment contains the website and an interactive timeline with fictional sample recordings. This establishes the product interface and hosting without accepting private recordings prematurely.

## Local alpha

1. Benchmark speech models on named hardware and a repeatable meeting corpus.
2. Build durable Windows microphone and system-output capture with separate tracks.
3. Add local transcription with provisional and finalized timestamped segments.
4. Build repository storage, search, audio-linked playback, corrections, export, and backup restoration.

Gate: a recoverable hour-long offline meeting, followed by search and playback from the saved archive.

## Connected alpha

1. Add explicit device pairing, revocation, authenticated LAN access, and encrypted remote routing.
2. Implement durable acknowledgments, resumable transfer, quotas, and backpressure.
3. Build Android microphone recording with an offline queue and automatic private sync.

Gate: capture through network interruptions and repository downtime, then deliver all saved audio without duplicates.

## MVP pilot

1. Add scoped hosted transcription jobs and an explicit local/hosted compute policy.
2. Add managed-service subscriptions, understandable metering, and spending caps.
3. Add scoped read-only MCP tools and operational recovery documentation.

Gate: independently usable self-hosting plus reliable managed connectivity and compute, with tested privacy and billing boundaries.

## Later

Validate Pixel Watch capture and battery behavior, then expand desktop and mobile platforms. Evaluate diarization, semantic search, and optional summaries after reliable recording and retrieval work.
