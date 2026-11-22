import json
import os
import time
import unittest
from pathlib import Path
from unittest.mock import patch

import jwt
from fastapi.testclient import TestClient

from app.aftersales_api import get_evidence_classifier
from app.config import settings
from app.main import app

COURT_INPUT = Path(__file__).resolve().parent / "fixtures" / "social" / "court_dispute_evidence_input.json"
URL = "/api/v1/aftersales/match-evidence"


def token(role="aftersales", **overrides):
    claims = {"sub": "u-1", "studio_id": "studio-1", "role": role, "exp": int(time.time()) + 600}
    claims.update(overrides)
    return jwt.encode(claims, settings.jwt_secret, algorithm=settings.jwt_algorithm)


def auth(value):
    return {"Authorization": f"Bearer {value}"}


def body():
    return {
        "start_ms": 0, "end_ms": 10_000, "coverage": [{"start_ms": 0, "end_ms": 10_000}],
        "utterances": [
            {"id": 1, "speaker": "system", "start_ms": 1_000, "end_ms": 2_000, "text": "你会不会打"},
            {"id": 2, "speaker": "player", "start_ms": 2_500, "end_ms": 3_500, "text": "测试发言"},
        ],
    }


def classify_all(label):
    def classify(window):
        return {"decisions": [{
            "id": target, "label": label, "reason": "测试依据",
            "target_evidence_ids": [target], "continues_from": None,
        } for target in window.targets]}
    return classify


class MatchEvidenceApiTests(unittest.TestCase):
    def setUp(self):
        self.client = TestClient(app)
        app.dependency_overrides[get_evidence_classifier] = lambda: classify_all("candidate")

    def tearDown(self):
        app.dependency_overrides.clear()

    def test_aftersales_gets_analysis(self):
        response = self.client.post(URL, json=body(), headers=auth(token()))
        self.assertEqual(response.status_code, 200, response.text)
        data = response.json()
        self.assertEqual(data["status"], "analyzed")
        self.assertEqual(data["candidate_count"], 1)
        self.assertEqual(data["events"][0]["evidence"][0]["id"], 2)

    def test_court_fixture_round_trip(self):
        app.dependency_overrides[get_evidence_classifier] = lambda: classify_all("review")
        payload = json.loads(COURT_INPUT.read_text(encoding="utf-8-sig"))
        response = self.client.post(URL, json=payload, headers=auth(token("admin")))
        self.assertEqual(response.status_code, 200, response.text)
        data = response.json()
        self.assertEqual(data["status"], "needs_review")
        self.assertEqual(len(data["review"]), data["target_utterances"])

    def test_missing_invalid_or_expired_token_is_401(self):
        expired = token(exp=int(time.time()) - 10)
        forged = jwt.encode({"sub": "u", "studio_id": "s", "role": "admin", "exp": time.time() + 60},
                            "wrong-secret", algorithm="HS256")
        no_studio = jwt.encode({"sub": "u", "role": "admin", "exp": time.time() + 60},
                               settings.jwt_secret, algorithm=settings.jwt_algorithm)
        for headers in ({}, auth(expired), auth(forged), auth(no_studio)):
            response = self.client.post(URL, json=body(), headers=headers)
            self.assertEqual(response.status_code, 401)
            self.assertEqual(response.json()["code"], "ERR_UNAUTHORIZED")

    def test_player_role_is_forbidden(self):
        response = self.client.post(URL, json=body(), headers=auth(token("player")))
        self.assertEqual(response.status_code, 403)
        self.assertEqual(response.json()["code"], "ERR_FORBIDDEN")

    def test_invalid_payloads_are_422(self):
        duplicate = body()
        duplicate["utterances"][1]["id"] = 1
        reversed_match = {**body(), "start_ms": 10_000, "end_ms": 5_000}
        bad_speaker = body()
        bad_speaker["utterances"][0]["speaker"] = "boss"
        for payload in (duplicate, reversed_match, bad_speaker):
            response = self.client.post(URL, json=payload, headers=auth(token()))
            self.assertEqual(response.status_code, 422, response.text)
            self.assertEqual(response.json()["code"], "ERR_VALIDATION")

    def test_unconfigured_model_is_503_but_auth_checked_first(self):
        app.dependency_overrides.clear()
        environment = patch.dict(os.environ)
        environment.start()
        self.addCleanup(environment.stop)
        os.environ.pop("EVIDENCE_MODEL_URL", None)
        os.environ.pop("EVIDENCE_MODEL", None)
        self.assertEqual(self.client.post(URL, json=body()).status_code, 401)
        response = self.client.post(URL, json=body(), headers=auth(token()))
        self.assertEqual(response.status_code, 503)
        self.assertEqual(response.json()["code"], "ERR_MODEL_NOT_CONFIGURED")


if __name__ == "__main__":
    unittest.main()
