"""Extract timed clips from dual-track segment media (player_mic / system_audio)."""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

TRACK_ALIASES = {
    "player": 0,
    "player_mic": 0,
    "mic": 0,
    "system": 1,
    "system_audio": 1,
}


def resolve_ffmpeg(ffmpeg: str | None = None) -> str:
    if ffmpeg:
        return ffmpeg
    env = os.environ.get("FFMPEG_PATH", "").strip()
    if env:
        return env
    found = shutil.which("ffmpeg")
    return found if found else "ffmpeg"


def extract_utterance_clip(
    media_path: Path,
    start_ms: int,
    end_ms: int,
    *,
    track: str = "player_mic",
    output_path: Path | None = None,
    ffmpeg: str | None = None,
    pad_ms: int = 0,
) -> Path:
    """Cut [start_ms, end_ms) from one audio track; times are relative to segment start."""
    if end_ms <= start_ms:
        raise ValueError("end_ms must be greater than start_ms")
    if not media_path.is_file():
        raise FileNotFoundError(f"segment not found: {media_path}")

    key = track.lower().replace("-", "_")
    if key not in TRACK_ALIASES:
        raise ValueError(f"unknown track {track!r}; expected one of {sorted(TRACK_ALIASES)}")

    start_ms = max(0, start_ms - pad_ms)
    end_ms = end_ms + pad_ms
    start_sec = start_ms / 1000.0
    duration_sec = (end_ms - start_ms) / 1000.0
    track_idx = TRACK_ALIASES[key]

    if output_path is None:
        output_path = media_path.with_name(
            f"{media_path.stem}_t{track_idx}_{start_ms}_{end_ms}.wav"
        )

    ffm = resolve_ffmpeg(ffmpeg)
    cmd = [
        ffm,
        "-hide_banner",
        "-loglevel",
        "error",
        "-y",
        "-ss",
        f"{start_sec:.3f}",
        "-t",
        f"{duration_sec:.3f}",
        "-i",
        str(media_path),
        "-map",
        f"0:a:{track_idx}",
        "-ac",
        "1",
        "-ar",
        "16000",
        "-c:a",
        "pcm_s16le",
        str(output_path),
    ]
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=120, check=False)
    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout or "ffmpeg failed").strip()
        raise RuntimeError(detail)

    size = output_path.stat().st_size if output_path.is_file() else 0
    if size < 44:
        raise RuntimeError("clip output missing or empty")
    return output_path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("media", type=Path, help="segment file (mp4/mkv with dual audio)")
    parser.add_argument("--start-ms", type=int, required=True)
    parser.add_argument("--end-ms", type=int, required=True)
    parser.add_argument("--track", default="player_mic", choices=sorted(TRACK_ALIASES))
    parser.add_argument("--pad-ms", type=int, default=0)
    parser.add_argument("--out", type=Path, default=None)
    parser.add_argument("--ffmpeg", default=None)
    args = parser.parse_args(argv)
    try:
        clip = extract_utterance_clip(
            args.media,
            args.start_ms,
            args.end_ms,
            track=args.track,
            output_path=args.out,
            ffmpeg=args.ffmpeg,
            pad_ms=args.pad_ms,
        )
    except (OSError, ValueError, RuntimeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    print(clip)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())