# SaidNest interface review

October 28, 2026. Applies to the public website and fictional timeline preview.

## Direction and scope

Taste page direction: **The name as a monument**, seed `281026`. App style: **Achromatic ledger**, seed `281026`, with its no-sidebar shell. No random redraws. The script records its host's UTC draw date; this review uses the project owner's October 28 date.

The page's idea: SaidNest's name brackets a place for spoken words, with an actual interactive transcript passage between the opening and closing wordmarks. The live element switches among the three fictional conversations and opens the corresponding transcript. The app's question is: **Where is the conversation, and which words do I need from it?** Its signature is a timestamp gutter beside readable passages, each copyable with its title and speaker.

The subject inventory was the SaidNest name and nest mark, microphone/output tracks, spoken passages, timestamps, conversations, owner-controlled hardware, a local repository, optional remote routing, optional hosted compute, and export into an agent's context. The page argues from a working passage to the planned archive, then answers what is available, what follows, payment, and offline access. It makes no invented performance, customer, or adoption claims.

The existing forest-green identity and Georgia/system font families are retained. The app uses the system face for reading, neutral surfaces and controls, and green only for timestamps and the existing brand mark. The header is 64px to accommodate readable labels and phone targets rather than the sheet's compact 48px example. The website's section compositions follow the actual content: a working reader rather than an unrelated photograph, a planned device/repository diagram rather than redundant process screenshots, and four real questions rather than manufactured answers to reach a quota.

## Before and after

| Area | Previous preview | Updated preview |
| --- | --- | --- |
| Transcript text | 11px, muted | 16px desktop/phone; 15px at tablet width, full ink |
| App title | 45px display serif | 28px desktop, 24px phone |
| Navigation | 226px sidebar and separate header | Shared top bar; phone project menu |
| Phone reading | List followed by the transcript below it | One pane at a time; explicit return to conversations |
| Search | Matching records only | Matching records and highlighted words |
| Export | Full transcript copy/download | Full transcript plus individual passages with context |
| Website | Floating notes, small uppercase labels, numbered features, resting shadows | Full-width wordmark, interactive passage, plain explanations, flat color fields |

The same synthetic dataset supplies both pages, including titles and excerpts. The long meeting title was edited to “A calmer workspace.” Its sample duration describes the fictional conversation; the reader explicitly labels the available text as an excerpt and says that playback is unavailable.

## Verification

- Existing six Worker behavioral tests passed. Syntax checks include the app, shared samples, and website scripts. The build contains nine assets and no runtime dependencies or external fonts, imagery, analytics, or scripts.
- Taste source lint passed for website HTML and app HTML/JavaScript with zero failures or warnings. Shared CSS was also checked in app mode; the final toast shadow was removed. Borders in the shared CSS belong to actual app controls, reader interactions, and the phone menu, rather than marketing ornaments.
- Reviewed built output in Chrome at 1440, 768, and 390px. Both pages had document scroll width equal to viewport width. Website headings remained at one or two lines at all three widths.
- Opened full-page desktop, tablet, and phone screenshots for both pages and the phone reader. Temporary images are in ignored `.taste-skill/before/` and `.taste-skill/after/`.
- Verified text search, visible matching-word highlighting, zero-result and clear-filter states, meeting filtering, conversation selection, clipboard contents for full transcripts and single passages, phone return focus, keyboard-operated menu, landing passage switching, and sample deep links.
- The download control is a real file link with a text Blob and the selected conversation's filename. The browser download-event observer stalled, but the saved `saidnest-sample-design.txt` was subsequently recovered and compared byte for byte with the expected title, date, speakers, timestamps, and all five passages. It matched. The test artifact was moved into the ignored design folder. The preparation notice itself does not claim download completion.
- Emulated reduced motion: all sampled website animations computed to `none` and scrolling to `auto`. Reset the emulation afterward. Browser warning/error logs were empty at the end of the local check.

The environment requires browser interaction through its provided browser controls. Taste's separate CDP browser inspector and its complete scoring measurements were therefore not run; DOM measurements and screenshots above are the evidence. This is a visual and functional review, not an accessibility certification or a full security audit. The existing Worker security policy and disabled recording capabilities are preserved.

## Critique

### Round 1

Seen in one second: the SaidNest name, followed by a readable passage rather than decorative audio art.

Idea: the name gives a visible home to the words between its opening and closing appearances.

Live thing: choose a fictional conversation and read its corresponding passage.

Layout words by section: hero=wordmark / showcase=reader / ownership=split / questions=question-and-answer rows / footer=inverted wordmark.

Scores: design 6.5 · usability 8 · creativity 6.5 · content 8 → overall 7.1. These are subjective self-review scores, not external awards or automated inspector scores. The idea is related to a transcript archive as a category; creativity is not claimed as unique.

Caps applied: the website intentionally uses one working interface demonstration rather than four pictures. Taste's picture-count cap keeps design at 6.5. Stock photos and redundant interface crops would not improve this preview's explanation. There is no footage; motion is limited to the wordmark, small section travel, and control feedback. The app has no decorative entrances or motion floor.

Top three problems and fixes:

1. Initial desktop wordmark occupied too little of the available width. Enlarged it to span the shared page frame; checked phone and tablet independently.
2. Phone search placeholder was clipped. Shortened it to “Search words” without changing the accessible label or search scope.
3. A copy notice persisted after changing the selected transcript or filters. Hide the previous notice whenever the view is rerendered.

### Round 2

Reopened the final screenshots after these fixes. Shared edges hold, website headings fit, transcript text is readable, and phone list/detail navigation preserves selection and returns focus. Scores remain 7.1 for the website with the same intentional picture-count cap. The app's primary question is answered by the list/reader; no charts or vanity metrics were added. Further differentiation should come from real recording and archive features as they become available.

No found photographs, footage, generated artwork, or new font files were used. The existing nest mark remains. All three conversations, speakers, dates, durations, and transcript passages are fictional examples; production archive content must replace these through the future authenticated repository integration.

## Local saved-recording interface extension

October 28, 2026: retained the existing Achromatic ledger direction for the separate loopback interface. Added source selection, native audio controls, timestamp seeking, local transcription action, refresh, and honest capture/inference notices. Cloud app.js still uses fictional samples; local.js is served into the shared shell only by the native service. No private archive content was viewed in browser automation.

Reviewed an isolated encrypted synthetic archive at 1440, 768, and 390px; DOM widths showed no horizontal overflow. Tablet/phone screenshots were opened; desktop capture showed scaled content inside its screenshot canvas, so that capture is limited evidence of desktop typography. Screenshots remain ignored under .taste-skill/local-timeline-*.png. Phone selection/return, source switching, highlighted search, no-results/clear, reload authorization, refresh, four-second audio readiness, and seeking from 3 seconds passed. The transcription button completed an actual local CUDA job on generated tone, saved a new revision, and displayed no speech. The copy action reported success; the browser clipboard reader returned an unrelated earlier fixture, so clipboard contents were not independently revalidated in this review. The download link has a Blob URL and correct selected source filename; download completion was not observed.

Taste source lint in app mode found zero failures and one warning for zero-padded clock times, which are intentional. The separate skill inspector/full scoring audit was not run; permitted browser DOM/screenshot checks were used. The extension adds controls within the established reader; no new decorative direction, imagery, fonts, or motion was introduced.
## Live recording controls

October 28, 2026: kept the existing app direction while adding real recording status, separate live source progress, draft labels, a primary Stop recording action, and one-minute playback selection. Playback is unavailable during capture to avoid recording its own system audio. The inactive retranscription action is hidden while recording; disabled controls have a visibly inactive treatment. Source level feedback uses plain words rather than decibel values.

Synthetic live-fixture review passed at 1440, 768, and 390px with no horizontal overflow in DOM measurements. Opened all three final screenshots under ignored .taste-skill/live-timeline-final-*.png. Verified live duration refresh, separate source selection, draft labels, protected stop transition to saved audio, and playback readiness afterward. The fixture is explicitly labeled and contains no private speech. Taste app source lint reports zero failures and one intentional clock-padding warning. Used permitted browser controls; the separate Taste inspector/scoring suite was not run. The first review revealed an inactive dark retranscription button competing with Stop; it is now hidden during capture and Stop is the primary action.
## Model comparison and reference review

October 28, 2026: retained the app direction and added version/model selectors, refinement progress, a model-comparison table, excerpt playback, draft reference text, explicit verification, and per-source score selection. Model wording does not check itself. References stay encrypted; changing words clears verification. An unscored cell says Not scored rather than displaying zero error.

Synthetic fixture testing verified restoring the original live revision, opening comparisons, loading an authenticated audio excerpt, copying a candidate as an unchecked draft, saving a checked reference, correct shared-reference scores, and returning scores to Not scored after editing/saving an unchecked draft. All visual/browser tests used fictional text and generated tone, never private recordings. DOM measurements at 1440, 768 and 390px showed no document overflow. Opened screenshots in ignored .taste-skill/quality-review-*.png. Review found narrow table headers and an overlaid save notification; table columns now retain a readable minimum width in their own horizontal scroller, and reference-save feedback stays in the form. A larger model and model agreement are explicitly distinguished from verified accuracy. The running owner review was preserved while references were being entered; it was not interrupted for a rebuild.

Taste app source lint reports zero failures and one intentional clock-format warning. The separate Taste inspector was not run; permitted browser controls supplied interaction, DOM and screenshot evidence. The optional speaker-bleed notice records owner-observed capture conditions and does not claim echo cancellation.

Playback follow-up: verified with generated tone that starting the reference player pauses the main player, starting main playback pauses reference playback, and switching source leaves both stopped. Detached controls are paused before replacement. This prevents interface playback overlap; it does not remove speaker bleed already recorded in the archive. Final synthetic layout screenshots are stored under ignored .taste-skill/quality-playback-final-*.png.
