from datetime import datetime
from typing import Literal
from uuid import UUID
from pydantic import BaseModel, Field


class ClientConfigResponse(BaseModel):
    target_processes: list[str]
    segment_duration_sec: int = 300
    local_retention_days: int = 3
    upload_part_size_bytes: int = 5 * 1024 * 1024
    idle_threshold_sec: int = 60
    post_exit_delay_sec: int = 30


class SessionCreateRequest(BaseModel):
    session_id: UUID
    order_ref: str | None = Field(default=None, max_length=128)
    note: str | None = Field(default=None, max_length=500)
    is_manual_backfill: bool = False
    started_at: datetime
    ended_at: datetime | None = None


class SessionFinishRequest(BaseModel):
    ended_at: datetime


class SegmentInitUploadRequest(BaseModel):
    segment_id: UUID
    session_id: UUID
    seq_no: int = Field(ge=1)
    codec_mode: str = Field(pattern="^(video_hw|audio_screenshots)$")
    file_size: int = Field(ge=0)
    sha256: str = Field(min_length=64, max_length=64)
    duration_ms: int = Field(ge=0)
    started_at: datetime
    ended_at: datetime


class EvidenceInterval(BaseModel):
    start_ms: int = Field(ge=0)
    end_ms: int = Field(gt=0)


class EvidenceUtterance(EvidenceInterval):
    id: int = Field(ge=1)
    speaker: Literal["player", "system"]
    text: str = Field(min_length=1, max_length=2000)


class MatchEvidenceRequest(BaseModel):
    """Single-match input; times are ms offsets from the order session start."""

    start_ms: int = Field(ge=0)
    end_ms: int = Field(gt=0)
    coverage: list[EvidenceInterval] = Field(max_length=2000)
    utterances: list[EvidenceUtterance] = Field(max_length=5000)


class EvidenceItem(EvidenceUtterance):
    reason: str
    target_evidence_ids: list[int]


class EvidenceEvent(EvidenceInterval):
    evidence: list[EvidenceItem]


class EvidenceReview(EvidenceInterval):
    id: int
    reason: str


class MatchEvidenceResponse(BaseModel):
    status: Literal["analyzed", "needs_review"]
    candidate_count: int
    events: list[EvidenceEvent]
    review: list[EvidenceReview]
    coverage_gaps: list[EvidenceInterval]
    target_utterances: int
    analyzed_utterances: int


class TranscriptPutRequest(BaseModel):
    """Full replacement transcript of one session; times are ms offsets from the session start."""

    coverage: list[EvidenceInterval] = Field(max_length=2000)
    utterances: list[EvidenceUtterance] = Field(max_length=5000)


class TranscriptPutResponse(BaseModel):
    session_id: str
    utterance_count: int
    coverage_count: int


class SessionMatchEvidenceRequest(BaseModel):
    start_ms: int = Field(ge=0)
    end_ms: int = Field(gt=0)
