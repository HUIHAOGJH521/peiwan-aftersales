import shutil
import subprocess
import sys
import types
import unittest
from unittest import mock
import wave
from pathlib import Path
from tempfile import TemporaryDirectory

from app.audio_clip import resolve_ffmpeg
from app.match_analysis import Interval, analyze_match
from app.match_recording import DEFAULT_INITIAL_PROMPT, DEFAULT_MODEL, LocalWhisper, Recording, transcribe_match


FFMPEG = shutil.which(resolve_ffmpeg())


@unittest.skipUnless(FFMPEG, "FFmpeg is required")
class MatchRecordingTests(unittest.TestCase):
    def create_recording(self, path, *sources):
        command = [FFMPEG, "-hide_banner", "-loglevel", "error"]
        for source in sources:
            command.extend(("-f", "lavfi", "-i", source))
        for index in range(len(sources)):
            command.extend(("-map", f"{index}:a"))
        subprocess.run(command + ["-c:a", "pcm_s16le", str(path)], check=True, timeout=15)

    def test_decodes_both_tracks_and_offsets_clipped_speech(self):
        def transcribe(path):
            with wave.open(str(path), "rb") as audio:
                pcm = audio.readframes(audio.getnframes())
            is_silent = not any(pcm)
            self.assertEqual(is_silent, path.stem == "system")
            return [(0.25, 0.75, "测试发言")] if not is_silent else []

        with TemporaryDirectory() as directory:
            path = Path(directory) / "dual.mkv"
            self.create_recording(path, "sine=frequency=440:duration=3", "anullsrc=d=3")
            match = Interval(11_000, 12_000)
            coverage, utterances, issues = transcribe_match(
                match, [Recording(10_000, 13_000, path)], transcribe,
            )
        self.assertEqual(coverage, [match])
        self.assertEqual([(item.speaker, item.start_ms, item.end_ms) for item in utterances],
                         [("player", 11_250, 11_750)])
        self.assertEqual(issues, [])

    def test_missing_second_track_is_not_reported_as_silence(self):
        with TemporaryDirectory() as directory:
            path = Path(directory) / "single.mkv"
            self.create_recording(path, "sine=frequency=440:duration=1")
            match = Interval(0, 1_000)
            coverage, utterances, issues = transcribe_match(
                match, [Recording(0, 1_000, path)], lambda _: [(0.25, 0.75, "测试发言")],
            )
        result = analyze_match(match, coverage, utterances,
                               lambda _: self.fail("Incomplete dual-track coverage must go to review"))
        self.assertEqual(result["status"], "needs_review")
        self.assertEqual(len(result["review"]), 1)
        self.assertEqual(result["coverage_gaps"], [{"start_ms": 0, "end_ms": 1_000}])
        self.assertEqual(issues, [{"start_ms": 0, "end_ms": 1_000,
                                  "track": "system", "reason": "audio_unavailable"}])

    def test_tail_coverage_uses_shorter_track(self):
        with TemporaryDirectory() as directory:
            path = Path(directory) / "short-system.mkv"
            self.create_recording(path, "anullsrc=d=2", "anullsrc=d=1")
            coverage, _, issues = transcribe_match(
                Interval(0, 2_000), [Recording(0, 2_000, path)], lambda _: [],
            )
        self.assertEqual(coverage, [Interval(0, 1_000)])
        self.assertEqual(issues, [{"start_ms": 1_000, "end_ms": 2_000,
                                  "track": "system", "reason": "audio_truncated"}])

    def test_adjacent_recordings_flow_into_one_evidence_event(self):
        def transcribe(path):
            text = "打手发言" if path.stem == "player" else "客户回复"
            return [(0.25, 0.75, text)]

        def classify(window):
            self.assertEqual(len(window.context), 4)
            first, second = window.targets
            sources = [item.id for item in window.context if item.speaker == "system"]
            return {"decisions": [
                {"id": first, "label": "candidate", "reason": "测试证据",
                 "target_evidence_ids": sources[:1], "continues_from": None},
                {"id": second, "label": "candidate", "reason": "测试证据",
                 "target_evidence_ids": sources[1:], "continues_from": first},
            ]}

        with TemporaryDirectory() as directory:
            path = Path(directory) / "dual.mkv"
            self.create_recording(path, "anullsrc=d=1", "anullsrc=d=1")
            match = Interval(0, 2_000)
            coverage, utterances, issues = transcribe_match(match, [
                Recording(1_000, 2_000, path), Recording(0, 1_000, path),
            ], transcribe)
        result = analyze_match(match, coverage, utterances, classify)
        self.assertEqual(issues, [])
        self.assertEqual(result["candidate_count"], 1)
        self.assertEqual(result["coverage_gaps"], [])
        self.assertEqual([row["start_ms"] for row in result["events"][0]["evidence"]],
                         [250, 1_250])


class LocalWhisperTests(unittest.TestCase):
    def build(self, **kwargs):
        calls = {}

        class FakeModel:
            def __init__(self, name, **options):
                calls["init"] = (name, options)

            def transcribe(self, audio, **options):
                calls["transcribe"] = (audio, options)
                segment = types.SimpleNamespace(start=0.0, end=1.0, text="老板")
                return iter([segment]), None

        module = types.SimpleNamespace(WhisperModel=FakeModel)
        with mock.patch.dict(sys.modules, {"faster_whisper": module}):
            whisper = LocalWhisper("fake-model", "zh", **kwargs)
        return whisper, calls

    def test_defaults_pass_prompt_vad_and_threads(self):
        whisper, calls = self.build()
        spans = list(whisper(Path("a.wav")))
        self.assertEqual(spans, [(0.0, 1.0, "老板")])
        self.assertEqual(calls["init"][0], "fake-model")
        self.assertEqual(calls["init"][1]["cpu_threads"], 0)
        options = calls["transcribe"][1]
        self.assertEqual(options["initial_prompt"], DEFAULT_INITIAL_PROMPT)
        self.assertEqual(options["hotwords"], DEFAULT_INITIAL_PROMPT)
        self.assertTrue(options["vad_filter"])
        self.assertFalse(options["condition_on_previous_text"])
        self.assertEqual(options["language"], "zh")
        for word in ("老板", "投诉", "押金", "陪玩", "打手", "纠纷", "护航", "对赌",
                     "证据不足", "工作室", "订单", "退款", "变声器", "店长", "曝光"):
            self.assertIn(word, DEFAULT_INITIAL_PROMPT)
        self.assertEqual(DEFAULT_MODEL, "large-v3-turbo")

    def test_custom_options_are_forwarded(self):
        whisper, calls = self.build(initial_prompt="", vad_filter=False, cpu_threads=6)
        list(whisper(Path("a.wav")))
        self.assertEqual(calls["init"][1]["cpu_threads"], 6)
        options = calls["transcribe"][1]
        self.assertIsNone(options["initial_prompt"])
        self.assertIsNone(options["hotwords"])
        self.assertFalse(options["vad_filter"])
