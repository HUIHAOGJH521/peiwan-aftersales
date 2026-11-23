"""SQLite transcript store. Every read and write is keyed by studio_id, so tenants never see each other's rows."""

from __future__ import annotations

import sqlite3
from datetime import datetime, timezone

from app.records import records_db_path

_SCHEMA = """
CREATE TABLE IF NOT EXISTS transcript_utterances (
    studio_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    utterance_id INTEGER NOT NULL,
    speaker TEXT NOT NULL,
    start_ms INTEGER NOT NULL,
    end_ms INTEGER NOT NULL,
    text TEXT NOT NULL,
    PRIMARY KEY (studio_id, session_id, utterance_id)
);
CREATE TABLE IF NOT EXISTS transcript_coverage (
    studio_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    seq INTEGER NOT NULL,
    start_ms INTEGER NOT NULL,
    end_ms INTEGER NOT NULL,
    PRIMARY KEY (studio_id, session_id, seq)
);
CREATE TABLE IF NOT EXISTS transcripts (
    studio_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (studio_id, session_id)
);
"""


def _connect() -> sqlite3.Connection:
    connection = sqlite3.connect(records_db_path())
    connection.row_factory = sqlite3.Row
    connection.executescript(_SCHEMA)
    return connection


def save_transcript(studio_id: str, session_id: str, coverage: list[tuple[int, int]],
                    utterances: list[tuple[int, str, int, int, str]]) -> None:
    """Replace the whole transcript of one session. Utterances are (id, speaker, start_ms, end_ms, text)."""
    key = (studio_id, session_id)
    connection = _connect()
    try:
        with connection:
            connection.execute("DELETE FROM transcript_utterances WHERE studio_id=? AND session_id=?", key)
            connection.execute("DELETE FROM transcript_coverage WHERE studio_id=? AND session_id=?", key)
            connection.executemany(
                "INSERT INTO transcript_utterances VALUES (?,?,?,?,?,?,?)",
                [(*key, *row) for row in utterances],
            )
            connection.executemany(
                "INSERT INTO transcript_coverage VALUES (?,?,?,?,?)",
                [(*key, seq, start, end) for seq, (start, end) in enumerate(coverage)],
            )
            connection.execute(
                "INSERT OR REPLACE INTO transcripts VALUES (?,?,?)",
                (*key, datetime.now(timezone.utc).isoformat()),
            )
    finally:
        connection.close()


def load_transcript(studio_id: str, session_id: str) -> dict | None:
    """Return {"coverage": [(start, end)], "utterances": [row dicts]} or None when this studio has none."""
    key = (studio_id, session_id)
    connection = _connect()
    try:
        if connection.execute(
            "SELECT 1 FROM transcripts WHERE studio_id=? AND session_id=?", key,
        ).fetchone() is None:
            return None
        coverage = connection.execute(
            "SELECT start_ms, end_ms FROM transcript_coverage WHERE studio_id=? AND session_id=? ORDER BY seq",
            key,
        ).fetchall()
        rows = connection.execute(
            "SELECT utterance_id, speaker, start_ms, end_ms, text FROM transcript_utterances "
            "WHERE studio_id=? AND session_id=? ORDER BY start_ms, utterance_id",
            key,
        ).fetchall()
    finally:
        connection.close()
    return {
        "coverage": [(row["start_ms"], row["end_ms"]) for row in coverage],
        "utterances": [
            {"id": r["utterance_id"], "speaker": r["speaker"], "start_ms": r["start_ms"],
             "end_ms": r["end_ms"], "text": r["text"]}
            for r in rows
        ],
    }
