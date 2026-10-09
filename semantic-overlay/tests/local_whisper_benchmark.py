"""Explicit local-video benchmark, never imported by the offline test runner.

Install faster-whisper in the ignored project-local venv before running.
Require existing local weights: this script never downloads or calls a provider.
Private results must stay in .local-asr/results, not Git or public demo media.
"""
import argparse
import json
import math
import statistics
import time
from pathlib import Path


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * fraction) - 1)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("video", type=Path)
    parser.add_argument("--model-path", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--threads", type=int, default=8)
    args = parser.parse_args()
    if not args.video.is_file() or not (args.model_path / "model.bin").is_file():
        parser.error("Existing video and local model weights are required")
    if not 1 <= args.threads <= 20:
        parser.error("threads must be 1..20")
    project = Path(__file__).resolve().parents[1]
    result_root = (project / ".local-asr" / "results").resolve()
    if not args.output.resolve().is_relative_to(result_root):
        parser.error("Private output must remain inside .local-asr/results")

    from faster_whisper import WhisperModel
    from faster_whisper.audio import decode_audio

    started = time.perf_counter()
    audio = decode_audio(str(args.video), sampling_rate=16000)
    duration = len(audio) / 16000
    decode_seconds = time.perf_counter() - started
    started = time.perf_counter()
    model = WhisperModel(str(args.model_path), device="cpu", compute_type="int8",
                         cpu_threads=args.threads, local_files_only=True)
    load_seconds = time.perf_counter() - started
    print(json.dumps({"stage": "loaded", "duration_seconds": duration,
                      "load_seconds": load_seconds, "device": "cpu-int8"}), flush=True)

    started = time.perf_counter()
    segments, info = model.transcribe(audio, language="en", beam_size=5,
                                      condition_on_previous_text=False,
                                      vad_filter=True)
    full = [{"start": s.start, "end": s.end, "text": s.text} for s in segments]
    full_seconds = time.perf_counter() - started
    print(json.dumps({"stage": "whole_file", "inference_seconds": full_seconds,
                      "segments": len(full)}), flush=True)

    # Disjoint windows deliberately expose boundary truncation; this is not
    # an application stream, and full-file output is not a ground-truth transcript.
    window_seconds = 4
    windows = []
    for offset in range(0, len(audio), window_seconds * 16000):
        clip = audio[offset:offset + window_seconds * 16000]
        started = time.perf_counter()
        segments, _ = model.transcribe(clip, language="en", beam_size=1,
                                       condition_on_previous_text=False,
                                       vad_filter=True)
        text = " ".join(s.text.strip() for s in segments).strip()
        elapsed = time.perf_counter() - started
        windows.append({"start": offset / 16000, "audio_seconds": len(clip) / 16000,
                        "inference_seconds": elapsed, "text": text})
        if len(windows) % 10 == 0:
            print(json.dumps({"stage": "windows", "completed": len(windows),
                              "last_seconds": elapsed}), flush=True)

    times = [w["inference_seconds"] for w in windows]
    summary = {
        "device": "cpu", "compute_type": "int8", "cpu_threads": args.threads,
        "duration_seconds": duration, "decode_seconds": decode_seconds,
        "model_load_seconds": load_seconds, "full_inference_seconds": full_seconds,
        "full_real_time_factor": full_seconds / duration,
        "full_segment_count": len(full), "warm_window_seconds": window_seconds,
        "warm_window_count": len(windows), "warm_inference_p50_seconds": statistics.median(times),
        "warm_inference_p95_seconds": percentile(times, .95),
        "warm_inference_max_seconds": max(times),
        "windows_slower_than_audio": sum(w["inference_seconds"] > w["audio_seconds"] for w in windows),
        "empty_windows": sum(not w["text"] for w in windows),
        "accuracy_status": "No reference transcript; WER and accuracy are unmeasured",
        "latency_scope": "Inference only, excludes capture, segmentation, queue, UI and cold start",
        "provider_requests": 0,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"summary": summary, "whole_file": full,
                                     "windows": windows}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
