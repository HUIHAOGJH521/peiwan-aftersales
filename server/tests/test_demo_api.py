import os
import tempfile
import unittest

from fastapi.testclient import TestClient

from app.main import app


class DemoApiTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.NamedTemporaryFile(suffix=".db", delete=False)
        self.tmp.close()
        os.environ["PEIWAN_RECORDS_DB"] = self.tmp.name
        self.client = TestClient(app)

    def tearDown(self):
        os.environ.pop("PEIWAN_RECORDS_DB", None)
        try:
            os.remove(self.tmp.name)
        except OSError:
            pass

    def test_court_sample_marks_dispute_lines(self):
        page = self.client.get("/")
        self.assertEqual(page.status_code, 200)
        self.assertIn("登录试点工作室", page.text)
        listed = self.client.get("/api/v1/demo/samples")
        self.assertEqual(listed.status_code, 200)
        ids = [item["id"] for item in listed.json()["samples"]]
        self.assertIn("court", ids)
        analyzed = self.client.post("/api/v1/demo/samples/court/analyze")
        self.assertEqual(analyzed.status_code, 200, analyzed.text)
        body = analyzed.json()["analysis"]
        self.assertEqual(body["status"], "needs_review")
        self.assertGreaterEqual(len(body["review"]), 3)
        saved = self.client.get("/api/v1/demo/records")
        self.assertEqual(saved.status_code, 200, saved.text)
        records = saved.json()["records"]
        self.assertEqual(records[0]["sample_id"], "court")
        self.assertGreaterEqual(records[0]["review_count"], 3)
        saved = self.client.post("/api/v1/demo/samples/court/verdicts", json={
            "utterance_id": 1,
            "start_ms": 20000,
            "text": "老板投诉押金",
            "verdict": "成立",
            "note": "先记一笔",
        })
        self.assertEqual(saved.status_code, 200, saved.text)
        again = self.client.post("/api/v1/demo/samples/court/verdicts", json={
            "utterance_id": 1,
            "start_ms": 20000,
            "text": "老板投诉押金",
            "verdict": "再看",
            "note": "",
        })
        self.assertEqual(again.status_code, 200, again.text)
        listed = self.client.get("/api/v1/demo/verdicts?sample_id=court")
        rows = listed.json()["verdicts"]
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["verdict"], "再看")
