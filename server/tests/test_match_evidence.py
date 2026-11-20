import json
import unittest
from unittest.mock import MagicMock

from app.match_analysis import ClassificationUnavailable, Interval, Utterance, analyze_match
from app.match_evidence import ChatClassifier


def speech(identifier, start, end, speaker="player", text="测试发言"):
    return Utterance(start, end, identifier, speaker, text)


def decision(identifier, label="candidate", link=None):
    return {
        "id": identifier, "label": label, "reason": "测试依据",
        "continues_from": link, "target_evidence_ids": [identifier],
    }


class MatchEvidenceTests(unittest.TestCase):
    def test_adjacent_files_keep_context_and_only_count_event_once(self):
        windows = []
        statements = [
            speech(1, 280_000, 282_000),
            speech(2, 297_000, 298_000, "system", "我是下单的人"),
            speech(3, 299_000, 300_000),
            speech(4, 301_000, 302_000),
            speech(5, 321_000, 322_000),
        ]

        def classify(window):
            windows.append(window)
            return {"decisions": [
                {1: decision(1, "excluded"), 3: decision(3),
                 4: decision(4, link=3), 5: decision(5)}[target]
                for target in window.targets
            ]}

        result = analyze_match(Interval(270_000, 330_000),
                               [Interval(270_000, 300_000), Interval(300_000, 330_000)],
                               statements, classify)
        self.assertEqual([(1, 3), (4, 5)], [window.targets for window in windows])
        self.assertIn(2, [item.id for item in windows[0].context])
        self.assertIn(3, [item.id for item in windows[1].context])
        self.assertEqual(result["candidate_count"], 2)
        self.assertEqual([3, 4], [row["id"] for row in result["events"][0]["evidence"]])
        self.assertEqual(result["events"][0]["end_ms"], 302_000)
        self.assertEqual(result["coverage_gaps"], [])

    def test_boundary_uses_neighbor_context_without_reclassifying_it(self):
        windows = []

        def classify(window):
            windows.append(window)
            return {"decisions": [
                decision(target, "excluded") if target == 1
                else decision(target, link=2 if target == 3 else None)
                for target in window.targets
            ]}

        result = analyze_match(Interval(0, 45_000), [Interval(0, 45_000)], [
            speech(1, 0, 1_000), speech(2, 25_000, 26_000), speech(3, 30_000, 31_000),
        ], classify)
        self.assertEqual([(1, 2), (3,)], [window.targets for window in windows])
        self.assertEqual([2, 3], [item.id for item in windows[1].context])
        self.assertEqual(result["candidate_count"], 1)

    def test_missing_media_and_crossing_speech_are_reported(self):
        def classify(window):
            self.assertEqual(tuple(item.id for item in window.context), window.targets)
            return {"decisions": [decision(target, "excluded") for target in window.targets]}

        result = analyze_match(Interval(1_000, 10_000), [
            Interval(0, 5_000), Interval(7_000, 10_000),
        ], [
            speech(1, 0, 1_100), speech(2, 4_900, 7_100), speech(3, 7_500, 8_000),
            speech(4, 9_000, 10_001, "system"), speech(5, 10_000, 11_000),
        ], classify)
        self.assertEqual(result["coverage_gaps"], [{"start_ms": 5_000, "end_ms": 7_000}])
        self.assertEqual([1, 2], [row["id"] for row in result["review"]])
        self.assertEqual(result["target_utterances"], 3)
        self.assertEqual(result["analyzed_utterances"], 1)
        self.assertEqual(result["status"], "needs_review")

    def test_uncertain_target_and_invalid_or_missing_model_results(self):
        statements = [speech(i, i * 100, i * 100 + 50) for i in range(1, 7)]
        result = analyze_match(Interval(0, 1_000), [Interval(0, 1_000)], statements,
                               lambda _: {"decisions": [
                                   decision(1), decision(2, "review"), decision(3, link=99),
                                   decision(4), decision(4), decision(6, "excluded"),
                               ]})
        self.assertEqual(result["candidate_count"], 1)
        self.assertEqual([2, 3, 4, 5], [row["id"] for row in result["review"]])
        self.assertEqual(result["analyzed_utterances"], 3)

    def test_silence_is_distinct_from_unavailable_analysis(self):
        def fail(_):
            raise ClassificationUnavailable

        match = Interval(0, 1_000)
        empty = analyze_match(match, [match], [], fail)
        failed = analyze_match(match, [match], [speech(1, 100, 200)], fail)
        unrecorded = analyze_match(match, [], [], fail)
        self.assertEqual(empty["status"], "analyzed")
        self.assertEqual(failed["status"], "needs_review")
        self.assertEqual(failed["analyzed_utterances"], 0)
        self.assertEqual(unrecorded["coverage_gaps"], [{"start_ms": 0, "end_ms": 1_000}])

    def test_model_request_only_contains_match_context(self):
        classifier = ChatClassifier("http://127.0.0.1:12345/v1/chat/completions", "test-model")
        classifier.opener = MagicMock()
        response = classifier.opener.open.return_value.__enter__.return_value
        response.read.return_value = json.dumps({"choices": [{"message": {"content": json.dumps(
            {"decisions": [decision(2, "excluded")]}
        )}}]}).encode("utf-8")
        result = analyze_match(Interval(100, 200), [Interval(100, 200)], [
            speech(1, 0, 100, text="outside"), speech(2, 120, 150),
        ], classifier)
        request = classifier.opener.open.call_args.args[0]
        payload = json.loads(request.data)
        context = json.loads(payload["messages"][1]["content"])
        self.assertEqual([item["id"] for item in context["utterances"]], [2])
        self.assertEqual(result["status"], "analyzed")

    def test_candidate_requires_visible_target_evidence(self):
        match = Interval(0, 1_000)
        result = analyze_match(match, [match], [speech(1, 0, 100)], lambda _: {
            "decisions": [{**decision(1), "target_evidence_ids": [99]}],
        })
        self.assertEqual(result["candidate_count"], 0)
        self.assertEqual([1], [row["id"] for row in result["review"]])

    def test_model_omitting_event_boundary_requires_review(self):
        match = Interval(0, 1_000)
        row = decision(1)
        del row["continues_from"]
        result = analyze_match(match, [match], [speech(1, 0, 100)],
                               lambda _: {"decisions": [row]})
        self.assertEqual(result["candidate_count"], 0)
        self.assertEqual([1], [item["id"] for item in result["review"]])


if __name__ == "__main__":
    unittest.main()
