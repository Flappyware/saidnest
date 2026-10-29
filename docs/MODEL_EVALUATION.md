# Local model evaluation and transcript refinement

Implemented October 28, 2026. All meeting audio, hypotheses, references, and revision selections stay in the local encrypted vault. The public website is still a fictional preview.

## Two passes, separate evidence

The live worker uses the selected model for four-second drafts and eight-second final windows. `tiny.en` remains the default. A later refinement runs the original audio through a selected model in bounded thirty-second windows, creates a new encrypted revision, and retains the live version. A larger model is a candidate, not automatically the most accurate choice.

The local timeline has a **Transcript version** selector, a **Refinement model** selector, **Create refined version**, and **Review accuracy**. The chosen refinement becomes the displayed version; selecting an earlier version restores it without deleting either revision. Revisions retain model hash/revision, runtime, decoding settings, window size, parent revision, and the source manifest hash where available. Older prototype revisions may lack newer fields.

To provision a pinned model and record with automatic later refinement:

```powershell
./scripts/windows/provision-whisper.ps1 -Model large-v3
./scripts/windows/start-recorder.ps1 -Live -LiveModel tiny.en -RefineModel large-v3 -Seconds 900
```

Available profiles are `tiny.en`, `small.en`, `large-v3-turbo`, and `large-v3`. URLs, hashes and revision pins are in `scripts/windows/models.json`. Provisioning does not send recordings anywhere. Audio capture remains independent of inference. A failed refinement retains the previous selected transcript and the original audio. Refinement is currently an explicit selected profile, not an automatically chosen “best model.”

## Comparing recognition quality

`evaluate` freezes twelve eight-second source excerpts, selected from six time strata using the original live model's nonempty outputs. Every compared model receives identical WAV bytes and greedy English decoding settings. This is a speech-heavy, baseline-selected sample, not a representative full-meeting corpus. Input hashes, set ID, model provenance, parameters, wall times, and hypotheses are recorded encrypted.

```powershell
dotnet run --project native/SaidNest.Capture -c Release --no-build -- evaluate --session SESSION_ID --install .saidnest/inference/install-large-v3-turbo.json
dotnet run --project native/SaidNest.Capture -c Release --no-build -- evaluation-summary --session SESSION_ID
```

In **Review accuracy**, load and listen to an excerpt, correct its reference text, and explicitly check **I listened and checked these words**. Model wording can be copied as a draft but is never automatically verified. Editing clears the check. References stay encrypted, and all compared models use the same checked reference. These references do not rewrite the transcript; they score model candidates on the frozen excerpts.

Word error rate is word edit distance divided by reference word count. Missing, substituted, and extra words count as errors, and the result can exceed 100%. Aggregate scoring weights reference words rather than averaging clip percentages. Empty references have no individual WER denominator; silence insertions remain observable through word/edit counts. Unverified references contribute nothing, and absent evidence displays **Not scored**. Model disagreement is explicitly separate from accuracy.

The versioned normalizer currently applies Unicode NFKC, lowercase, apostrophe normalization, and word tokenization. It ignores most punctuation but does not canonicalize written numbers or alternate spellings of names. Those conventions can affect scores. Keep raw references/hypotheses and rescore consistently when changing normalization. Small checked samples must not be represented as whole-meeting accuracy.

A separate scripted speech/silence corpus provides reproducible smoke scores. Synthetic WER is labeled separately from meeting WER. The raw silence test bypasses the quiet-signal guard, so hallucinated words and rejected timestamp segments are visible. A quiet threshold is not qualified VAD; discarded output is not proof that a model intrinsically handles silence correctly.

## Comparing throughput

Each sampled excerpt is also decoded at four seconds and eight seconds for each source, sequentially, matching the current live worker's fresh-process schedule. The report includes worst two-source batch time, simulated start backlog, finish lag, and missed four-second compute deadlines. It includes process/model loading and window rendering; OS file caches may already be warm. It excludes first installation, browser refresh, and a fresh recording's capture/persistence delay. This is not a measurement of capture-to-visible-word latency.

`replay` additionally processes the complete saved session on the same rolling schedule, keeping compute timing and a simulated queue. It saves an encrypted replay revision without replacing the owner-selected transcript:

```powershell
dotnet run --project native/SaidNest.Capture -c Release --no-build -- replay --session SESSION_ID --install .saidnest/inference/install-large-v3-turbo.json
```

The replay runs as fast as possible; it is not a new live recording. Deadline calculations exclude the short end-of-recording tail. Recorder I/O concurrency, UI latency, a busy GPU, thermals, and real device failure still require a supervised live test. A fresh-process result does not establish the limits of a resident model server or another inference runtime.

## What to measure next

This Discord recording has owner-reported speaker bleed: monitor output was audible on the BRIO microphone. The condition is stored as encrypted recording context and appears with evaluation results. A microphone track is a source, not a verified person. References should include every audible word on the selected source, including leaked remote voices; unintelligible passages should remain unchecked. Scores are available separately for mic/output as well as together. Cross-track duplicate speech is a capture/echo issue, not automatically a recognition error.

Use a headphone recording as the next clean baseline, then a separate speaker test. Evaluate reference-based acoustic echo cancellation using the synchronized output signal, with delay/drift handling and explicit double-talk tests. Preserve raw audio and save any processed microphone signal as a derivative with provenance. Do not blank the mic whenever output is active or remove matching text blindly; either can lose simultaneous speech. VAD, alignment, diarization and AEC solve different problems.

Relevant implementation references are [WebRTC AudioProcessing](https://webrtc.googlesource.com/src/+/refs/heads/main/api/audio/audio_processing.h) and [Windows Voice Capture DSP](https://learn.microsoft.com/en-us/windows/win32/medfound/voicecapturedmo). No echo-cancellation implementation was added in this change.

Expand checked references to names, accents, overlapping voices, boundary words, low-level speech, and verified silence. Compare a resident worker to remove repeated model loading. Measure word timing and diarization separately from word recognition. Preserve corrections and revision provenance before enabling automatic model promotion or semantic agent access.
