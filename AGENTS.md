# SaidNest project instructions

## Frontend design

Use the project-local **Taste anti-slop skill** for frontend design, implementation, restyling, and visual review. Read [.agents/skills/design-taste-frontend/SKILL.md](.agents/skills/design-taste-frontend/SKILL.md) before that work and follow the applicable references.

- The public website follows the skill's page workflow.
- The transcript timeline, recording controls, pairing, settings, and other working screens follow its separate `references/app-screens.md` workflow. Marketing composition rules must not dictate app information density.
- Read [docs/FRONTEND_DESIGN.md](docs/FRONTEND_DESIGN.md) for SaidNest's brief, constraints, provenance, and verification expectations.
- Preserve the existing stack and working behavior. Scope a review, restyle, or redesign to the user's request.
- Use the browser tools permitted in the active environment for screenshots and interaction checks. If the skill's browser inspector cannot be used, inspect through those tools and report the measurements that were not run. Do not claim an unperformed audit.

## Product boundaries

SaidNest is a local-first audio and transcription archive. Local use must remain independent of cloud sign-in and subscriptions. Preserve the documented security and privacy boundaries when changing the interface.

The current deployed app is a preview with fictional conversations. Keep that status visible and describe recording, transcription, pairing, sync, and billing as planned until they work. Do not introduce private recordings, transcripts, credentials, or account configuration into public source, design artifacts, or screenshots.

Use the public [architecture](docs/ARCHITECTURE.md), [roadmap](docs/ROADMAP.md), and [plan review](docs/PLAN_REVIEW.md) as implementation context. The full internal plan and deployment records belong in the separate private operations repository.
