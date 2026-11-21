import json
import shutil
import unittest
from pathlib import Path

from app.audio_clip import resolve_ffmpeg
from app.match_analysis import Interval, Utterance, analyze_match
from app.match_recording import guess_mixed_speaker

FIXTURE_DIR = Path(__file__).resolve().parent / "fixtures" / "social"
MANIFEST = FIXTURE_DIR / "delta_escort_match.json"
TRANSCRIPT = FIXTURE_DIR / "delta_escort_transcript.json"
PEIWAN_TRANSCRIPT = FIXTURE_DIR / "peiwan_complaint_transcript.json"
FFMPEG = shutil.which(resolve_ffmpeg())
HAVE_VIDEO = (FIXTURE_DIR / "bili_BV1A7sS6GEiX.mp4").is_file()


class GuessSpeakerTests(unittest.TestCase):
    def test_boss_hint_goes_to_system(self) -> None:
        self.assertEqual(guess_mixed_speaker("哈喽你是不是也在打三角洲"), "system")

    def test_escort_hint_goes_to_player(self) -> None:
        self.assertEqual(guess_mixed_speaker("哥我求你了放我出去"), "player")

    def test_shop_dispute_hints(self) -> None:
        self.assertEqual(guess_mixed_speaker("老板摸多少红我们就要杀多少人"), "system")
        self.assertEqual(guess_mixed_speaker("我俩一把杀了十一个人头"), "player")


@unittest.skipUnless(HAVE_VIDEO and FFMPEG and TRANSCRIPT.is_file(), "social transcript fixture missing")
class SocialEvidencePipelineTests(unittest.TestCase):
    def test_saved_transcript_runs_evidence_scan(self) -> None:
        payload = json.loads(TRANSCRIPT.read_text(encoding="utf-8"))
        match = Interval(payload["start_ms"], payload["end_ms"])
        coverage = [Interval(**part) for part in payload["coverage"]]
        utterances = [Utterance(**row) for row in payload["utterances"]]

        def classify(window):
            decisions = []
            for target in window.targets:
                item = next(row for row in window.context if row.id == target)
                if "求你" in item.text or "感谢" in item.text:
                    label = "excluded"
                else:
                    label = "excluded"
                decisions.append({
                    "id": target,
                    "label": label,
                    "reason": "社媒试跑：无辱骂语境",
                    "target_evidence_ids": [target],
                    "continues_from": None,
                })
            return {"decisions": decisions}

        analysis = analyze_match(match, coverage, utterances, classify)
        self.assertGreater(analysis["target_utterances"], 0)
        self.assertEqual(analysis["candidate_count"], 0)
        self.assertIn("mono_mixed_no_separate_track", {
            row["reason"] for row in payload.get("media_issues", [])
        })


@unittest.skipUnless(
    (FIXTURE_DIR / "bili_BV1hGsW6SEYV.mp4").is_file() and PEIWAN_TRANSCRIPT.is_file(),
    "peiwan transcript fixture missing",
)
class PeiwanComplaintPipelineTests(unittest.TestCase):
    def test_dispute_transcript_has_review_or_complaint_words(self) -> None:
        payload = json.loads(PEIWAN_TRANSCRIPT.read_text(encoding="utf-8"))
        merged = " ".join(row["text"] for row in payload["utterances"])
        self.assertTrue(any(word in merged for word in ("投訴", "投诉", "老闆", "老板", "對賭", "对赌", "人頭", "人头")))
        analysis = payload.get("analysis")
        self.assertIsNotNone(analysis)
        self.assertGreater(analysis["target_utterances"], 0)
        review_reasons = {row["reason"] for row in analysis["review"]}
        self.assertTrue(analysis["review"] and "待复核" in "".join(review_reasons))


if __name__ == "__main__":
    unittest.main()