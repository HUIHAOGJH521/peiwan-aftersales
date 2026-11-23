import os
import tempfile
import time
import unittest
from datetime import datetime, timezone
from unittest.mock import patch
from uuid import uuid4

import jwt
from fastapi.testclient import TestClient

from app.aftersales_api import get_evidence_classifier
from app.client_api import get_client_state
from app.client_state import ClientState, Session
from app.config import settings
from app.main import app
from app.transcripts import load_transcript

STUDIO_A = "studio-a"
STUDIO_B = "studio-b"


def auth(role="aftersales", studio=STUDIO_A):
    claims = {"sub": "u-1", "studio_id": studio, "role": role, "exp": int(time.time()) + 600}
    value = jwt.encode(claims, settings.jwt_secret, algorithm=settings.jwt_algorithm)
    return {"Authorization": f"Bearer {value}"}


def transcript_body():
    return {
        "coverage": [{"start_ms": 0, "end_ms": 10_000}],
        "utterances": [
            {"id": 1, "speaker": "system", "start_ms": 1_000, "end_ms": 2_000, "text": "hello"},
            {"id": 2, "speaker": "player", "start_ms": 2_500, "end_ms": 3_500, "text": "world"},
        ],
    }


def classify_all(window):
    return {"decisions": [{
        "id": target, "label": "candidate", "reason": "fake",
        "target_evidence_ids": [target], "continues_from": None,
    } for target in window.targets]}


class SessionTranscriptTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        patcher = patch.dict(os.environ, {"PEIWAN_RECORDS_DB": os.path.join(tmp.name, "t.db")})
        patcher.start()
        self.addCleanup(patcher.stop)
        self.state = ClientState()
        self.session_id = str(uuid4())
        self.state.sessions[self.session_id] = Session(
            self.session_id, STUDIO_A, "p-1", "d-1", None, None, False,
            datetime.now(timezone.utc), None,
        )
        app.dependency_overrides[get_client_state] = lambda: self.state
        app.dependency_overrides[get_evidence_classifier] = lambda: classify_all
        self.addCleanup(app.dependency_overrides.clear)
        self.client = TestClient(app)
        self.put_url = f"/api/v1/aftersales/sessions/{self.session_id}/transcript"
        self.post_url = f"/api/v1/aftersales/sessions/{self.session_id}/match-evidence"

    def put(self, **kwargs):
        return self.client.put(self.put_url, json=transcript_body(), headers=kwargs.get("headers", auth()))

    def test_put_then_analyze(self):
        response = self.put()
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(response.json()["utterance_count"], 2)
        result = self.client.post(self.post_url, json={"start_ms": 0, "end_ms": 10_000}, headers=auth("admin"))
        self.assertEqual(result.status_code, 200, result.text)
        self.assertEqual(result.json()["status"], "analyzed")
        self.assertGreaterEqual(result.json()["candidate_count"], 1)

    def test_put_replaces_previous_transcript(self):
        self.put()
        body = transcript_body()
        body["utterances"] = body["utterances"][:1]
        self.client.put(self.put_url, json=body, headers=auth())
        stored = load_transcript(STUDIO_A, self.session_id)
        self.assertEqual([row["id"] for row in stored["utterances"]], [1])

    def test_cross_tenant_is_404(self):
        self.assertEqual(self.put().status_code, 200)
        other = auth(studio=STUDIO_B)
        self.assertEqual(self.client.put(self.put_url, json=transcript_body(), headers=other).status_code, 404)
        response = self.client.post(self.post_url, json={"start_ms": 0, "end_ms": 10_000}, headers=other)
        self.assertEqual(response.status_code, 404)
        self.assertIsNone(load_transcript(STUDIO_B, self.session_id))

    def test_unknown_session_and_missing_transcript_are_404(self):
        missing = f"/api/v1/aftersales/sessions/{uuid4()}"
        self.assertEqual(self.client.put(missing + "/transcript", json=transcript_body(), headers=auth()).status_code, 404)
        self.assertEqual(self.client.post(missing + "/match-evidence", json={"start_ms": 0, "end_ms": 5}, headers=auth()).status_code, 404)
        # Session exists but no transcript uploaded yet.
        response = self.client.post(self.post_url, json={"start_ms": 0, "end_ms": 5}, headers=auth())
        self.assertEqual(response.status_code, 404)

    def test_roles_and_missing_token(self):
        body = {"start_ms": 0, "end_ms": 10_000}
        for role in ("player", "viewer"):
            self.assertEqual(self.put(headers=auth(role)).status_code, 403)
            self.assertEqual(self.client.post(self.post_url, json=body, headers=auth(role)).status_code, 403)
        self.assertEqual(self.client.put(self.put_url, json=transcript_body()).status_code, 401)

    def test_model_not_configured_is_503(self):
        self.put()
        app.dependency_overrides.pop(get_evidence_classifier)
        with patch.dict(os.environ):
            os.environ.pop("EVIDENCE_MODEL_URL", None)
            os.environ.pop("EVIDENCE_MODEL", None)
            response = self.client.post(self.post_url, json={"start_ms": 0, "end_ms": 10_000}, headers=auth())
        self.assertEqual(response.status_code, 503)
        self.assertEqual(response.json()["code"], "ERR_MODEL_NOT_CONFIGURED")

    def test_duplicate_utterance_ids_are_422(self):
        body = transcript_body()
        body["utterances"][1]["id"] = 1
        response = self.client.put(self.put_url, json=body, headers=auth())
        self.assertEqual(response.status_code, 422)


if __name__ == "__main__":
    unittest.main()
