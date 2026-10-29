# Speakr and WhisperX reference review

Reviewed October 28, 2026 from upstream documentation. No Speakr code was copied or installed.

[Speakr](https://github.com/murtaza-nasir/speakr) separates its interface from configurable transcription backends and supports speaker diarization through a self-hosted WhisperX service. Its transcript-linked playback and per-recording processing settings are useful workflow references. The application repository lists AGPL-3.0, while SaidNest remains MIT. This review adopts product ideas, not implementation code.

[WhisperX](https://github.com/m-bain/whisperX) combines Whisper recognition, forced alignment for word timestamps, and pyannote diarization for who-spoke-when labels. Speaker labels are not verified names, and its documentation acknowledges limitations with overlap and diarization. Model access/provisioning and licensing must be checked separately from the library.

The related [WhisperX ASR service](https://github.com/murtaza-nasir/whisperx-asr-service) documents an ASR → alignment → diarization pipeline, serialized GPU execution, and optional preloaded models. It also identifies the service as alpha and unauthenticated. Any SaidNest integration needs a private worker boundary and SaidNest authorization; publishing that service directly would not satisfy our repository access model.

For SaidNest, retain the original microphone/output separation, then optionally run word alignment and anonymous speaker labeling during the later refinement pass. The system-output track can contain several friends; the microphone can contain room pickup or echo. Preserve unknown/overlapping intervals, let the owner assign names, and evaluate speaker error separately from WER. Cross-recording voice profiles are outside this change. The next experiment is an optional locally provisioned WhisperX worker, not a required dependency of recording or offline archive access.
