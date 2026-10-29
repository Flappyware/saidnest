# SaidNest frontend design

Adopted October 28, 2026. This records the design workflow; the existing website and preview have not yet been redesigned or audited with Taste.

## Taste adoption

Use [Taste](https://www.tasteskill.dev/), installed as [design-taste-frontend](../.agents/skills/design-taste-frontend/SKILL.md), for frontend work. It supplies separate workflows for public pages and app screens, subject-specific art direction, checks for generic generated patterns, and screenshot-based critique.

The skill is development tooling. It adds no application dependencies or runtime services. The application build copies only `public/`; the skill and design working files are not published as website assets.

## Product brief

SaidNest gives recordings and their words a home on a system the owner controls. The website should make the local archive, optional connections, and optional hosted compute understandable. Its primary working screen helps someone find a conversation, read a timestamped passage, and copy or export the part they need.

Design from this product's material: spoken words, source tracks, timestamps, recoverable recordings, transcript passages, and a timeline. Prefer a real interface or a clearly labeled fictional example as evidence. Do not invent customer quotes, recognition scores, recording counts, performance results, or unsupported features.

For the app, prioritize legibility, fast retrieval, and clear controls. Capture, delivery, and transcription are separate states. When implemented, recording needs visible source activity, pause/stop controls, and understandable interruption/recovery feedback. A color or animation alone must not communicate a critical state.

## Constraints

- Keep the current plain HTML/CSS/JavaScript stack unless a separate implementation decision changes it.
- Preserve offline operation. Ship fonts, icons, imagery, and other required assets locally with their licenses; system fonts are also suitable. Do not add runtime font/image CDNs, trackers, or third-party scripts to satisfy a design suggestion.
- Preserve the content security policy and microphone restrictions of the preview. Permission changes require an actual recording feature and its security design.
- Keep keyboard access, visible focus, accessible names, readable contrast, reduced-motion support, and useful small-screen layouts.
- Use synthetic content for design previews and checks. Keep preview status visible; authentication and private archive data must not be implied by a decorative mockup.
- Treat Taste's imagery and motion recipes as design guidance within these product constraints. The working app follows the app-screen reference; a transcript reader should remain efficient and readable.

## Verification for frontend changes

Record the current screen before a redesign. Inspect the final built result at desktop, tablet, and phone widths (Taste uses 1440, 768, and 390 pixels), open the screenshots, and critique the actual output. Check keyboard focus, overflow, reduced motion where applicable, and the affected interactions. For the existing preview, preserve search, filtering, transcript selection, copy, download, and empty states.

Use Taste's source lint and browser inspector where the environment permits them. If browser tooling restrictions require the available browser controls instead, record that substitution and any unmeasured checks. Address substantive failures and document intentional exceptions. Store scratch reports, images, and direction sheets in ignored `.taste-skill/`; commit durable design decisions or public review findings under `docs/` using synthetic content only.

No Taste visual audit is a prerequisite for backend-only changes. A design score does not replace functional, accessibility, privacy, or security checks.

## Source and maintenance

- Upstream: [Leonxlnx/taste-skill](https://github.com/Leonxlnx/taste-skill).
- Installed skill: `design-taste-frontend`, upstream path `skills/taste-skill`.
- Verified source revision: [`12af471f627f866411b4bd785b19646d3672784c`](https://github.com/Leonxlnx/taste-skill/tree/12af471f627f866411b4bd785b19646d3672784c/skills/taste-skill). All 39 installed files matched this revision when adopted.
- The CLI-generated [skills-lock.json](../skills-lock.json) records its source and content hash. The committed skill files preserve the reviewed version; an unpinned reinstall may fetch newer upstream content.
- License: MIT, copyright 2026 Leonxlnx. The upstream [license notice](licenses/taste-skill-MIT.txt) is retained. Fonts, photographs, and other assets suggested by the skill retain their own licenses and require separate review before use.

The requested installation used the following command from the repository root, with CLI telemetry disabled for that run:

```sh
npx --yes skills add Leonxlnx/taste-skill --agent codex --skill design-taste-frontend --yes --copy
```

Keep the installation project-local. Review upstream changes, update the lock file and source revision together, and retain the license when updating. Do not add generated audits or real archive content to the vendored skill.
