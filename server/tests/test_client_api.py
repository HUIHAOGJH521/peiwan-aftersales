import unittest
from uuid import uuid4

from fastapi.testclient import TestClient

from app.client_api import get_client_state
from app.client_state import PILOT_STUDIO, PILOT_USER, ClientState
from app.main import app


def login_body(**overrides):
    body = {
        "studio_code": "pilot_01",
        "username": "pw001",
        "password": "pilot-dev-pass",
        "device_fp": "sha256-device-1",
        "gpu_encoder": "amf",
        "client_version": "0.1.0",
    }
    body.update(overrides)
    return body


class ClientApiTests(unittest.TestCase):
    def setUp(self):
        self.state = ClientState()
        app.dependency_overrides[get_client_state] = lambda: self.state
        self.client = TestClient(app)
        logged = self.client.post("/api/v1/auth/client-login", json=login_body())
        self.assertEqual(logged.status_code, 200, logged.text)
        payload = logged.json()
        self.assertEqual(payload["player"]["id"], PILOT_USER)
        self.assertEqual(payload["player"]["studio_id"], PILOT_STUDIO)
        self.headers = {"Authorization": f"Bearer {payload['access_token']}"}

    def tearDown(self):
        app.dependency_overrides.clear()

    def test_bad_password_is_401(self):
        response = self.client.post("/api/v1/auth/client-login", json=login_body(password="nope"))
        self.assertEqual(response.status_code, 401)
        self.assertEqual(response.json()["code"], "ERR_UNAUTHORIZED")

    def test_config_requires_player_device(self):
        response = self.client.get("/api/v1/client/config", headers=self.headers)
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(response.json()["target_processes"], ["DeltaForceClient-Win64-Shipping.exe"])
        self.assertEqual(response.json()["upload_part_size_bytes"], 5 * 1024 * 1024)

    def test_session_and_resumable_upload(self):
        session_id = str(uuid4())
        segment_id = str(uuid4())
        created = self.client.post("/api/v1/client/sessions", headers=self.headers, json={
            "session_id": session_id,
            "order_ref": "BX20261118001",
            "note": "护航",
            "is_manual_backfill": False,
            "started_at": "2026-11-18T14:00:00+08:00",
        })
        self.assertEqual(created.status_code, 200, created.text)
        self.assertEqual(created.json()["status"], "recording")

        digest = "a" * 64
        first = self.client.post("/api/v1/client/segments/init-upload", headers=self.headers, json={
            "segment_id": segment_id,
            "session_id": session_id,
            "seq_no": 1,
            "codec_mode": "video_hw",
            "file_size": 6 * 1024 * 1024,
            "sha256": digest,
            "duration_ms": 300000,
            "started_at": "2026-11-18T14:00:00+08:00",
            "ended_at": "2026-11-18T14:05:00+08:00",
        })
        self.assertEqual(first.status_code, 200, first.text)
        body = first.json()
        self.assertFalse(body["already_completed"])
        self.assertEqual([part["part_number"] for part in body["presigned_parts"]], [1, 2])

        self.state.record_part(segment_id, 1, '"etag-1"')
        second = self.client.post("/api/v1/client/segments/init-upload", headers=self.headers, json={
            "segment_id": segment_id,
            "session_id": session_id,
            "seq_no": 1,
            "codec_mode": "video_hw",
            "file_size": 6 * 1024 * 1024,
            "sha256": digest,
            "duration_ms": 300000,
            "started_at": "2026-11-18T14:00:00+08:00",
            "ended_at": "2026-11-18T14:05:00+08:00",
        })
        resumed = second.json()
        self.assertEqual(resumed["uploaded_parts"], [{"part_number": 1, "etag": '"etag-1"'}])
        self.assertEqual([part["part_number"] for part in resumed["presigned_parts"]], [2])
        self.assertEqual(resumed["upload_id"], body["upload_id"])

        done = self.client.post(
            f"/api/v1/client/segments/{segment_id}/complete-upload",
            headers=self.headers,
            json={"parts": [{"part_number": 1, "etag": '"etag-1"'}, {"part_number": 2, "etag": '"etag-2"'}]},
        )
        self.assertEqual(done.status_code, 200, done.text)
        self.assertEqual(done.json()["upload_status"], "completed")
        self.assertEqual(done.json()["asr_status"], "pending")
        self.assertEqual(self.state.enqueued, [segment_id])

        again = self.client.post(
            f"/api/v1/client/segments/{segment_id}/complete-upload",
            headers=self.headers,
            json={"parts": [{"part_number": 1, "etag": '"etag-1"'}, {"part_number": 2, "etag": '"etag-2"'}]},
        )
        self.assertEqual(again.status_code, 200, again.text)
        self.assertEqual(self.state.enqueued, [segment_id])

        finished = self.client.patch(
            f"/api/v1/client/sessions/{session_id}/finish",
            headers=self.headers,
            json={"ended_at": "2026-11-18T15:12:30+08:00"},
        )
        self.assertEqual(finished.status_code, 200, finished.text)
        self.assertEqual(finished.json()["status"], "recorded")


if __name__ == "__main__":
    unittest.main()
