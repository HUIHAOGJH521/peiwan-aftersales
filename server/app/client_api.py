"""Client login, order sessions, and resumable segment uploads."""

from uuid import UUID

from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field

from app.auth import Principal, issue_token, require_player
from app.client_state import PART_SIZE, ClientState, Device, Segment, Session, User, pending_part_numbers
from app.errors import ApiError
from app.schemas import (
    ClientConfigResponse,
    SegmentInitUploadRequest,
    SessionCreateRequest,
    SessionFinishRequest,
)

router = APIRouter()
_state = ClientState()


def get_client_state() -> ClientState:
    return _state


class LoginRequest(BaseModel):
    studio_code: str = Field(min_length=1, max_length=64)
    username: str = Field(min_length=1, max_length=64)
    password: str = Field(min_length=1, max_length=128)
    device_fp: str = Field(min_length=8, max_length=128)
    gpu_encoder: str = Field(default="unknown", max_length=32)
    client_version: str = Field(default="0.1.0", max_length=32)


class UploadedPart(BaseModel):
    part_number: int = Field(ge=1)
    etag: str = Field(min_length=1, max_length=128)


class CompleteUploadRequest(BaseModel):
    parts: list[UploadedPart] = Field(min_length=1, max_length=10_000)


def _player_view(user: User) -> dict:
    return {"id": user.id, "display_name": user.display_name, "studio_id": user.studio_id}


def _tokens(user: User, device: Device) -> dict:
    principal = Principal(user.id, user.studio_id, user.role, device.id)
    return {
        "access_token": issue_token(principal, kind="access", ttl_sec=86_400),
        "refresh_token": issue_token(principal, kind="refresh", ttl_sec=30 * 86_400),
        "expires_in": 86_400,
        "player": _player_view(user),
    }


@router.post("/api/v1/auth/client-login")
def client_login(body: LoginRequest, state: ClientState = Depends(get_client_state)) -> dict:
    user, device = state.authenticate(
        body.studio_code, body.username, body.password, body.device_fp, body.gpu_encoder, body.client_version,
    )
    return _tokens(user, device)


@router.get("/api/v1/client/config", response_model=ClientConfigResponse)
def client_config(_: Principal = Depends(require_player)) -> ClientConfigResponse:
    return ClientConfigResponse(target_processes=["DeltaForceClient-Win64-Shipping.exe"])


@router.post("/api/v1/client/sessions")
def upsert_session(
    body: SessionCreateRequest,
    principal: Principal = Depends(require_player),
    state: ClientState = Depends(get_client_state),
) -> dict:
    if body.ended_at is not None and body.ended_at < body.started_at:
        raise ApiError(422, "ERR_VALIDATION", "结束时间不能早于开始时间")
    session = state.upsert_session(Session(
        str(body.session_id),
        principal.studio_id,
        principal.user_id,
        principal.device_id or "",
        body.order_ref,
        body.note,
        body.is_manual_backfill,
        body.started_at,
        body.ended_at,
    ))
    return {"session_id": session.id, "status": session.status}


@router.patch("/api/v1/client/sessions/{session_id}/finish")
def finish_session(
    session_id: UUID,
    body: SessionFinishRequest,
    principal: Principal = Depends(require_player),
    state: ClientState = Depends(get_client_state),
) -> dict:
    session = state.finish_session(str(session_id), principal.studio_id, principal.user_id, body.ended_at)
    return {"session_id": session.id, "status": session.status, "ended_at": session.ended_at}


def _presign(segment: Segment, part_number: int) -> str:
    return (
        f"https://upload.local/{segment.storage_key}"
        f"?uploadId={segment.upload_id}&partNumber={part_number}"
    )


@router.post("/api/v1/client/segments/init-upload")
def init_upload(
    body: SegmentInitUploadRequest,
    principal: Principal = Depends(require_player),
    state: ClientState = Depends(get_client_state),
) -> dict:
    if body.ended_at < body.started_at:
        raise ApiError(422, "ERR_VALIDATION", "切片结束时间不能早于开始时间")
    segment, completed = state.init_segment(Segment(
        id=str(body.segment_id),
        studio_id=principal.studio_id,
        player_id=principal.user_id,
        session_id=str(body.session_id),
        seq_no=body.seq_no,
        codec_mode=body.codec_mode,
        storage_key=f"{principal.studio_id}/{body.session_id}/{body.segment_id}.mp4",
        upload_id=None,
        file_size=body.file_size,
        sha256=body.sha256.lower(),
        duration_ms=body.duration_ms,
        started_at=body.started_at,
        ended_at=body.ended_at,
    ), principal.user_id)
    uploaded = [{"part_number": number, "etag": etag} for number, etag in sorted(segment.parts.items())]
    pending = [] if completed else pending_part_numbers(segment.file_size, segment.parts)
    return {
        "already_completed": completed,
        "upload_id": segment.upload_id,
        "part_size": PART_SIZE,
        "uploaded_parts": uploaded,
        "presigned_parts": [
            {"part_number": number, "url": _presign(segment, number)} for number in pending
        ],
    }


@router.post("/api/v1/client/segments/{segment_id}/complete-upload")
def complete_upload(
    segment_id: UUID,
    body: CompleteUploadRequest,
    principal: Principal = Depends(require_player),
    state: ClientState = Depends(get_client_state),
) -> dict:
    segment = state.complete_segment(
        str(segment_id),
        principal.studio_id,
        principal.user_id,
        [(part.part_number, part.etag) for part in body.parts],
    )
    return {
        "segment_id": segment.id,
        "upload_status": segment.upload_status,
        "asr_status": segment.asr_status,
    }
