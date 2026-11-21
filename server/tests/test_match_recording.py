import shutil
import subprocess
import unittest
import wave
from pathlib import Path
from tempfile import TemporaryDirectory

from app.audio_clip import resolve_ffmpeg
from app.match_analysis import Interval, analyze_match
from app.match_recording import Recording, transcribe_match


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
