"""Local SQLite log of demo evidence runs. Survives process restarts."""

from __future__ import annotations

import json
import os
import sqlite3
from datetime import datetime, timezone
from pathlib import Path


def records_db_path() -> Path:
    override = os.environ.get("PEIWAN_RECORDS_DB", "").strip()
    if override:
        path = Path(override)
    else:
        path = Path(__file__).resolve().parents[1] / "data" / "records.db"
    path.parent.mkdir(parents=True, exist_ok=True)
    return path


def _connect() -> sqlite3.Connection:
    connection = sqlite3.connect(records_db_path())
    connection.row_factory = sqlite3.Row
    connection.execute(
        """
        CREATE TABLE IF NOT EXISTS demo_reviews (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            sample_id TEXT NOT NULL,
            title TEXT NOT NULL,
            status TEXT NOT NULL,
            review_count INTEGER NOT NULL,
            candidate_count INTEGER NOT NULL,
            review_json TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """
    )
    return connection


def save_review(sample_id: str, title: str, analysis: dict) -> dict:
    created_at = datetime.now(timezone.utc).isoformat()
    review = analysis.get("review", [])
    with _connect() as connection:
        cursor = connection.execute(
            """
            INSERT INTO demo_reviews
                (sample_id, title, status, review_count, candidate_count, review_json, created_at)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (
                sample_id,
                title,
                analysis.get("status", ""),
                len(review),
                int(analysis.get("candidate_count", 0)),
                json.dumps(review, ensure_ascii=False),
                created_at,
            ),
        )
        record_id = int(cursor.lastrowid)
    return {
        "id": record_id,
        "sample_id": sample_id,
        "title": title,
        "status": analysis.get("status", ""),
        "review_count": len(review),
        "candidate_count": int(analysis.get("candidate_count", 0)),
        "created_at": created_at,
    }


def list_reviews(limit: int = 20) -> list[dict]:
    with _connect() as connection:
        rows = connection.execute(
            """
            SELECT id, sample_id, title, status, review_count, candidate_count, created_at
            FROM demo_reviews
            ORDER BY id DESC
            LIMIT ?
            """,
            (limit,),
        ).fetchall()
    return [dict(row) for row in rows]


VERDICTS = ("成立", "不成立", "再看")


def save_verdict(
    sample_id: str,
    utterance_id: int,
    start_ms: int,
    text: str,
    verdict: str,
    note: str,
) -> dict:
    if verdict not in VERDICTS:
        raise ValueError("invalid verdict")
    note = note.strip()[:200]
    created_at = datetime.now(timezone.utc).isoformat()
    with _connect() as connection:
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS demo_verdicts (
                sample_id TEXT NOT NULL,
                utterance_id INTEGER NOT NULL,
                start_ms INTEGER NOT NULL,
                text TEXT NOT NULL,
                verdict TEXT NOT NULL,
                note TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (sample_id, utterance_id)
            )
            """
        )
        connection.execute(
            """
            INSERT INTO demo_verdicts
                (sample_id, utterance_id, start_ms, text, verdict, note, created_at)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(sample_id, utterance_id) DO UPDATE SET
                start_ms=excluded.start_ms,
                text=excluded.text,
                verdict=excluded.verdict,
                note=excluded.note,
                created_at=excluded.created_at
            """,
            (sample_id, utterance_id, start_ms, text, verdict, note, created_at),
        )
    return {
        "sample_id": sample_id,
        "utterance_id": utterance_id,
        "start_ms": start_ms,
        "text": text,
        "verdict": verdict,
        "note": note,
        "created_at": created_at,
    }


def list_verdicts(sample_id: str | None = None) -> list[dict]:
    with _connect() as connection:
        connection.execute(
            """
            CREATE TABLE IF NOT EXISTS demo_verdicts (
                sample_id TEXT NOT NULL,
                utterance_id INTEGER NOT NULL,
                start_ms INTEGER NOT NULL,
                text TEXT NOT NULL,
                verdict TEXT NOT NULL,
                note TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (sample_id, utterance_id)
            )
            """
        )
        if sample_id:
            rows = connection.execute(
                """
                SELECT sample_id, utterance_id, start_ms, text, verdict, note, created_at
                FROM demo_verdicts WHERE sample_id=? ORDER BY start_ms
                """,
                (sample_id,),
            ).fetchall()
        else:
            rows = connection.execute(
                """
                SELECT sample_id, utterance_id, start_ms, text, verdict, note, created_at
                FROM demo_verdicts ORDER BY created_at DESC LIMIT 50
                """
            ).fetchall()
    return [dict(row) for row in rows]
