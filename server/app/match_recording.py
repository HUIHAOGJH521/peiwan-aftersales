"""Transcribe a match from local dual-track recordings and optionally analyze it."""

import argparse
import json
import math
import shutil
import subprocess
import wave
from collections.abc import Callable, Iterable
from dataclasses import asdict, dataclass
from pathlib import Path
from tempfile import TemporaryDirectory

from app.audio_clip import extract_utterance_clip, resolve_ffmpeg
from app.match_analysis import Interval, Utterance, analyze_match
from app.match_evidence import ChatClassifier


@dataclass(frozen=True)
class Recording(Interval):
    path: Path


def guess_mixed_speaker(text: str) -> str:
    """Heuristic labels for mono Bilibili mixes (escort POV); not for production dual-track."""
    boss_hits = sum(
        marker in text
        for marker in (
            "老板", "老闆", "哈喽", "电话", "你是不是", "全了", "店长", "店長",
            "投诉", "投訴", "对赌", "對賭", "下单", "开庭", "開庭", "押金", "押金", "曝光",
        )
    )
    player_hits = sum(
        marker in text
        for marker in (
            "求你了", "感谢哥", "兄弟", "带", "撤离", "小哥", "我俩", "我倆", "人头", "人頭", "青屠", "殺",
        )
    )
    if boss_hits > player_hits:
        return "system"
    if player_hits > boss_hits:
        return "player"
    return "player"


DEFAULT_MODEL = "large-v3-turbo"
DEFAULT_INITIAL_PROMPT = (
    "以下是三角洲行动陪玩售后的普通话对话,涉及老板、投诉、押金、陪玩、打手、纠纷、护航、"
    "对赌、证据不足、工作室、订单、退款、变声器、店长、曝光。"
)


class LocalWhisper:
    """faster-whisper wrapper.

    faster-whisper 1.2.1 在 condition_on_previous_text=False 时,每个 30 秒窗口解码后都会
    重置 prompt,initial_prompt 只作用于第一个窗口。因此提示词同时通过 hotwords 传入:
    hotwords 在每个窗口都会拼进 prompt,长音频后段也能受益。
    """

    def __init__(
        self,
        model: str,
        language: str,
        *,
        initial_prompt: str | None = DEFAULT_INITIAL_PROMPT,
        vad_filter: bool = True,
        cpu_threads: int = 0,
    ) -> None:
        from faster_whisper import WhisperModel

        self.model = WhisperModel(
            model, device="cpu", compute_type="int8", cpu_threads=cpu_threads,
        )
        self.language = language
        self.initial_prompt = initial_prompt or None
        self.vad_filter = vad_filter

    def __call__(self, audio: Path) -> Iterable[tuple[float, float, str]]:
        segments, _ = self.model.transcribe(
            str(audio),
            language=self.language,
            beam_size=5,
            condition_on_previous_text=False,
            initial_prompt=self.initial_prompt,
            hotwords=self.initial_prompt,
            vad_filter=self.vad_filter,
        )
        for segment in segments:
            yield segment.start, segment.end, segment.text

def transcribe_match(
    match: Interval,
    recordings: list[Recording],
    transcribe: Callable[[Path], Iterable[tuple[float, float, str]]],
    *,
    ffmpeg: str | None = None,
    audio_layout: str = "dual",
    mono_speaker_policy: str = "mixed_guess",
) -> tuple[list[Interval], list[Utterance], list[dict]]:
    if audio_layout not in ("dual", "mono_mixed"):
        raise ValueError("Unsupported audio layout")
    if mono_speaker_policy not in ("mixed_guess", "narrator_as_player"):
        raise ValueError("Unsupported mono speaker policy")
    mono = audio_layout == "mono_mixed"
    clips = [recording for recording in recordings
             if recording.start_ms < match.end_ms and match.start_ms < recording.end_ms]
    clips.sort(key=lambda recording: recording.start_ms)
    for left, right in zip(clips, clips[1:]):
        if left.end_ms > right.start_ms:
            raise ValueError("Overlapping recording intervals")

    coverage: list[Interval] = []
    utterances: list[Utterance] = []
    issues: list[dict] = []
    for recording in clips:
        start = max(recording.start_ms, match.start_ms)
        end = min(recording.end_ms, match.end_ms)
        tracks = []
        with TemporaryDirectory(prefix="peiwan-asr-") as temp:
            speakers = ("player",) if mono else ("player", "system")
            for speaker in speakers:
                try:
                    clip = extract_utterance_clip(
                        recording.path, start - recording.start_ms, end - recording.start_ms,
                        track=speaker, output_path=Path(temp) / f"{speaker}.wav", ffmpeg=ffmpeg,
                    )
                    with wave.open(str(clip), "rb") as audio:
                        duration = math.ceil(audio.getnframes() * 1000 / audio.getframerate())
                    if duration <= 0:
                        raise RuntimeError("Empty audio track")
                except (OSError, RuntimeError, subprocess.TimeoutExpired, wave.Error, EOFError):
                    if mono and speaker == "player":
                        issues.append({"start_ms": start, "end_ms": end,
                                       "track": speaker, "reason": "audio_unavailable"})
                    elif not mono:
                        issues.append({"start_ms": start, "end_ms": end,
                                       "track": speaker, "reason": "audio_unavailable"})
                    continue
                try:
                    spans = list(transcribe(clip))
                    for begin, finish, text in spans:
                        if not (math.isfinite(begin) and math.isfinite(finish)
                                and 0 <= begin < finish and isinstance(text, str)):
                            raise ValueError("Invalid ASR span")
                except (OSError, RuntimeError, ValueError, TypeError):
                    issues.append({"start_ms": start, "end_ms": end,
                                   "track": speaker, "reason": "transcription_failed"})
                    continue
                decoded_end = min(end, start + duration)
                tracks.append(decoded_end)
                if decoded_end < end:
                    issues.append({"start_ms": decoded_end, "end_ms": end,
                                   "track": speaker, "reason": "audio_truncated"})
                for begin, finish, text in spans:
                    first = start + math.floor(begin * 1000)
                    last = min(decoded_end, start + math.ceil(finish * 1000))
                    if first < last and text.strip():
                        if not mono:
                            label = speaker
                        elif mono_speaker_policy == "narrator_as_player":
                            label = "player"
                        else:
                            label = guess_mixed_speaker(text)
                        utterances.append(Utterance(first, last, len(utterances) + 1,
                                                    label, text.strip()))
        if len(tracks) == 2:
            coverage.append(Interval(start, min(tracks)))
        elif mono and tracks:
            coverage.append(Interval(start, tracks[0]))
            issues.append({
                "start_ms": start,
                "end_ms": end,
                "track": "system",
                "reason": "mono_mixed_no_separate_track",
            })
    utterances.sort(key=lambda item: (item.start_ms, item.id))
    return coverage, utterances, issues


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--model", default=DEFAULT_MODEL,
                        help="Whisper cache name (e.g. large-v3-turbo, small) or local model directory")
    parser.add_argument("--initial-prompt", default=DEFAULT_INITIAL_PROMPT,
                        help="Domain prompt for Whisper; pass an empty string to disable")
    parser.add_argument("--no-vad", action="store_true", help="Disable the VAD filter")
    parser.add_argument("--cpu-threads", type=int, default=0, help="CPU threads (0 = library default)")
    parser.add_argument("--language", default="zh")
    parser.add_argument("--ffmpeg", help="FFmpeg executable (defaults to FFMPEG_PATH or PATH)")
    parser.add_argument("--transcript-only", action="store_true")
    args = parser.parse_args()
    try:
        manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
        match = Interval(manifest["start_ms"], manifest["end_ms"])
        layout = manifest.get("audio_layout", "dual")
        mono_policy = manifest.get("mono_speaker_policy", "mixed_guess")
        recordings = [
            Recording(item["start_ms"], item["end_ms"], args.manifest.parent / item["path"])
            for item in manifest["recordings"]
        ]
    except (OSError, ValueError, KeyError, TypeError):
        parser.error("无法读取录像清单或清单格式不正确")
    ffmpeg = shutil.which(resolve_ffmpeg(args.ffmpeg))
    if not ffmpeg:
        parser.error("找不到 FFmpeg，请设置 FFMPEG_PATH 或 --ffmpeg")
    classifier = None
    if not args.transcript_only:
        try:
            classifier = ChatClassifier.from_environment()
        except ValueError:
            parser.error("请配置证据模型地址和模型名，或使用 --transcript-only")
    try:
        transcriber = LocalWhisper(
            args.model, args.language, initial_prompt=args.initial_prompt,
            vad_filter=not args.no_vad, cpu_threads=args.cpu_threads,
        )
    except ImportError:
        parser.error("请安装 local-asr 依赖")
    except (OSError, RuntimeError, ValueError):
        parser.error("无法加载本地 Whisper 模型，请检查缓存或 --model 路径")
    try:
        coverage, utterances, issues = transcribe_match(
            match,
            recordings,
            transcriber,
            ffmpeg=ffmpeg,
            audio_layout=layout,
            mono_speaker_policy=mono_policy,
        )
    except ValueError:
        parser.error("本局录像时间范围发生重叠，请检查录像清单")
    result = {
        **asdict(match),
        "audio_layout": layout,
        "mono_speaker_policy": mono_policy,
        "coverage": [asdict(part) for part in coverage],
        "utterances": [asdict(item) for item in utterances],
        "media_issues": issues,
    }
    if classifier is not None:
        result["analysis"] = analyze_match(match, coverage, utterances, classifier)
    print(json.dumps(result, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
