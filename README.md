# SaidNest

**A home for what was said.**

SaidNest is an open-source project for local audio capture, transcription, and a personal timeline. The repository you control will own the archive; optional hosted services will provide remote connections and transcription compute.

- Website: [saidnest.com](https://saidnest.com)
- Interactive preview: [saidnest.com/app/](https://saidnest.com/app/)
- [Architecture](docs/ARCHITECTURE.md) and [roadmap](docs/ROADMAP.md)

## Current release

This first release contains the public website, an interactive timeline using fictional sample conversations, and a Cloudflare Worker health endpoint. Search, transcript selection, clipboard copy, and transcript downloads work in the preview. Recording, transcription, pairing, authentication, sync, and billing are not implemented yet. The preview does not request microphone access, accept uploads, or persist transcript content.

## Development

Requires Node.js 22.18 or newer. No package dependencies are needed for this starter.

```sh
npm run dev
npm run check
npm test
npm run build
```

Open http://localhost:8787. The development server uses the same Worker handler with a local static-assets adapter. Build creates `dist/` and an ignored `.deploy/bundle.json` containing asset hashes and the Worker module.

## Deployment

The production target is Cloudflare Workers with Static Assets. `src/deployment.json` defines the version settings; `src/worker.js` handles API routes, canonical redirects, and security headers. Deployment can use Cloudflare's [direct upload API](https://developers.cloudflare.com/workers/static-assets/direct-upload/): register the built manifest, upload requested asset buckets, then upload the Worker with the returned completion token and an `ASSETS` binding. Never commit upload tokens or deployment credentials.

`/api/health` reports the release and explicitly disabled backend capabilities. Unimplemented API routes return 404 and unsupported methods return 405. No customer repository is exposed by deploying this preview.

Pull requests run syntax checks, behavioral tests, and the build through GitHub Actions. Production deployment is operated separately; a passing CI run does not automatically publish a release.

## Licensing

Application code is MIT licensed. Future model weights and runtimes may have separate licenses, which must be documented before distribution. Private account configuration and internal operating records belong in the separate operations repository.
