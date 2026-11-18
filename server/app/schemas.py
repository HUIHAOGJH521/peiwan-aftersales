from datetime import datetime
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
