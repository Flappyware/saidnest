"""Offline, file-based development benchmark. Never a streaming or meeting-quality claim."""
import argparse
import concurrent.futures
import hashlib
import json
import re
import statistics
import subprocess
import time
import wave
from datetime import datetime, timezone
from pathlib import Path


def words(text):
    return re.findall(r"[a-z0-9]+", text.lower())


def word_error_rate(reference, hypothesis):
    expected, actual = words(reference), words(hypothesis)
    if not expected:
        return None
    previous = list(range(len(actual) + 1))
    for index, expected_word in enumerate(expected, 1):
        row = [index]
        for column, actual_word in enumerate(actual, 1):
            row.append(min(previous[column] + 1, row[-1] + 1, previous[column - 1] + (expected_word != actual_word)))
        previous = row
    return previous[-1] / len(expected)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--install", default=".saidnest/inference/install.json")
    parser.add_argument("--corpus", default=".saidnest/corpus/en-synthetic-v0")
    parser.add_argument("--out", default=".saidnest/benchmarks")
    parser.add_argument("--repeats", type=int, default=2)
    options = parser.parse_args()
    if not 1 <= options.repeats <= 5:
        parser.error("--repeats must be 1–5")
    install = json.loads(Path(options.install).read_text(encoding="utf-8-sig"))
    corpus = Path(options.corpus).resolve()
    model = Path(install["model"])
    if hashlib.sha256(model.read_bytes()).hexdigest() != install["modelSha256"]:
        raise RuntimeError("Model checksum differs from provisioned source")
    manifest = json.loads((corpus / "manifest.json").read_text(encoding="utf-8-sig"))
    for entry in manifest["files"]:
        path = corpus / entry["file"]
        if path.parent != corpus or hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
            raise RuntimeError("Corpus file differs from frozen manifest")
    output = Path(options.out).resolve() / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    output.mkdir(parents=True, exist_ok=False)

    def run(device, case, label):
        audio = corpus / f"{case}.wav"
        with wave.open(str(audio)) as source:
            duration = source.getnframes() / source.getframerate()
        prefix = output / label
        command = [install["executable"], "--model", str(model), "--file", str(audio), "--language", "en",
                   "--threads", "8", "--best-of", "1", "--beam-size", "1", "--temperature", "0", "--temperature-inc", "0",
                   "--output-json", "--output-file", str(prefix)]
        if device == "cpu":
            command.append("--no-gpu")
        start = time.perf_counter()
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        elapsed = time.perf_counter() - start
        prefix.with_suffix(".stdout.txt").write_text(result.stdout, encoding="utf-8")
        prefix.with_suffix(".stderr.txt").write_text(result.stderr, encoding="utf-8")
        if result.returncode:
            raise RuntimeError(f"Benchmark failed ({label}); inspect its local stderr log")
        transcript = json.loads(prefix.with_suffix(".json").read_text(encoding="utf-8"))
        text = " ".join(part["text"].strip() for part in transcript.get("transcription", []))
        reference = audio.with_suffix(".reference.txt").read_text(encoding="utf-8-sig")
        # A CUDA enumeration message alone is insufficient: require an actual CUDA compute buffer.
        cuda_compute = bool(re.search(r"whisper_backend_init_gpu: using CUDA\d+ backend", result.stderr)) and bool(re.search(r"CUDA\d+ total size", result.stderr))
        if device == "cuda" and not cuda_compute:
            raise RuntimeError(f"CUDA compute not established ({label}); inspect backend log before making a GPU claim")
        timings = {name.strip(): float(value) for name, value in re.findall(r"whisper_print_timings:\s*([^=\n]+?)\s*=\s*([0-9.]+) ms", result.stderr)}
        return {"label": label, "device": device, "case": case, "audioSeconds": duration, "wallSeconds": elapsed,
                "wallRealTimeFactor": elapsed / duration, "wordErrorRate": word_error_rate(reference, text),
                "referenceWords": len(words(reference)), "hypothesisWords": len(words(text)),
                "silenceHasText": bool(text.strip()) if case == "silence" else None,
                "silenceHasSpokenText": text.strip() not in ("", "[BLANK_AUDIO]", "[SILENCE]", "(silence)") if case == "silence" else None,
                "segmentsPastAudioEnd": sum(part.get("offsets", {}).get("to", 0) > duration * 1000 + 100 for part in transcript.get("transcription", [])),
                "cudaComputeBufferVerified": cuda_compute, "engineTimingsMs": timings, "text": text, "command": command}

    runs = []
    for device in ("cpu", "cuda"):
        for case in ("speech", "silence"):
            for repeat in range(options.repeats):
                result = run(device, case, f"{device}-{case}-{repeat}")
                runs.append(result)
                print(json.dumps({key: result[key] for key in ("label", "wallSeconds", "wallRealTimeFactor", "wordErrorRate", "silenceHasText")}), flush=True)
    concurrent_start = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
        pending = [executor.submit(run, "cuda", "speech", f"cuda-concurrent-{index}") for index in range(2)]
        concurrent_runs = [future.result() for future in pending]
    concurrent_wall = time.perf_counter() - concurrent_start
    summary = {
        "version": 1, "runtime": install["runtime"], "runtimeBuild": install["runtimeBuild"],
        "modelSha256": install["modelSha256"], "modelRevision": install["modelRevision"],
        "corpus": manifest, "runs": runs, "concurrentRuns": concurrent_runs,
        "concurrentWallSeconds": concurrent_wall,
        "concurrentWallRTFPerSourceDuration": concurrent_wall / concurrent_runs[0]["audioSeconds"],
        "medianSpeechWallSeconds": {device: statistics.median(run["wallSeconds"] for run in runs if run["device"] == device and run["case"] == "speech") for device in ("cpu", "cuda")},
        "limitations": ["Synthetic English smoke corpus; not a meeting-quality evaluation or production model selection.",
                        "Each run starts a new process and loads a model. Engine timing fields are separate from total wall time.",
                        "Concurrent jobs reuse synthetic speech; they do not reproduce microphone echo, drift, or conversational overlap.",
                        "No streaming latency, finalization latency, sustained thermal behavior, or peak memory measurement."]}
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"Saved offline benchmark evidence: {output / 'summary.json'}")


if __name__ == "__main__":
    main()
