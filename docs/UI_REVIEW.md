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
- The download control is a real file link with a text Blob and the selected conversation's filename. The browser download-event observer stalled; completion and saved-file bytes could not be confirmed through that observer. Full transcript clipboard contents were verified independently. No download-completion claim is made from the preparation notice.
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
