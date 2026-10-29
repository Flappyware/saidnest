# SaidNest

**A home for what was said.**

SaidNest is an open-source project for local audio capture, transcription, and a personal timeline. The repository you control will own the archive; optional hosted services will provide remote connections and transcription compute.

- Website: [saidnest.com](https://saidnest.com)
- Interactive preview: [saidnest.com/app/](https://saidnest.com/app/)
- [Architecture](docs/ARCHITECTURE.md), [roadmap](docs/ROADMAP.md), and [plan review](docs/PLAN_REVIEW.md)
- [Local model comparison and refinement](docs/MODEL_EVALUATION.md), [Speakr/WhisperX review](docs/SPEAKR_REVIEW.md)
- [Idle refinement and GPU policy](docs/REFINEMENT_QUEUE.md), [Windows background auto-start](docs/WINDOWS_AUTOSTART.md)

## Current release

The deployed release contains the public website, an interactive timeline using fictional sample conversations, and a Cloudflare Worker health endpoint. Search, transcript selection, clipboard copy, and transcript downloads work in the preview. The preview does not request microphone access, accept uploads, or persist transcript content.

A separate [Windows prototype](docs/WINDOWS_PROTOTYPE.md) provides explicit source selection, encrypted audio chunks, and crash-recovery inspection. Use `Start-Live-SaidNest.cmd` for rolling real-time English text during capture, or `Start-SaidNest.cmd` for audio only. Open `Start-Local-SaidNest.cmd` for the authenticated local timeline, playback, search, and timestamped copy/export. Live inference uses eight-second windows, later refinement uses thirty-second windows, and browser playback uses one-minute windows. Model comparison and encrypted checked-reference scoring support measured model choices. Inference requires the provisioned CUDA runtime. It remains a development prototype; pairing, remote authentication, sync, and billing are planned.

## Development

Requires Node.js 22.18 or newer. No package dependencies are needed for this starter.

```sh
npm run dev
npm run check
npm test
npm run build
npm run preview
```

Open http://localhost:8787. The development server uses the same Worker handler with a local static-assets adapter. Build creates `dist/` and an ignored `.deploy/bundle.json` containing asset hashes and the Worker module. Use `npm run preview` after building to check the files in `dist/` instead of the development sources; run only one of the two servers at a time.

Frontend work uses the project-local [Taste anti-slop skill](.agents/skills/design-taste-frontend/SKILL.md). Read [AGENTS.md](AGENTS.md) and the [frontend design brief](docs/FRONTEND_DESIGN.md) before changing the website or app screens. The brief records the installed source revision, license, offline constraints, and visual verification workflow.

The first [interface review](docs/UI_REVIEW.md) records the website and app redesign, responsive checks, and remaining validation limits. The preview also supports highlighted search matches and copying individual timestamped passages with their context.

## Deployment

The production target is Cloudflare Workers with Static Assets. `src/deployment.json` defines the version settings; `src/worker.js` handles API routes, canonical redirects, and security headers. Deployment can use Cloudflare's [direct upload API](https://developers.cloudflare.com/workers/static-assets/direct-upload/): register the built manifest, upload requested asset buckets, then upload the Worker with the returned completion token and an `ASSETS` binding. Never commit upload tokens or deployment credentials.

`/api/health` reports the release and explicitly disabled backend capabilities. Unimplemented API routes return 404 and unsupported methods return 405. No customer repository is exposed by deploying this preview.

Pull requests run syntax checks, behavioral tests, and the build through GitHub Actions. Production deployment is operated separately; a passing CI run does not automatically publish a release.

## Licensing

Application code is MIT licensed. Future model weights and runtimes may have separate licenses, which must be documented before distribution. Private account configuration and internal operating records belong in the separate operations repository.
