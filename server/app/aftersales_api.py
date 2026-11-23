from collections.abc import Callable

from uuid import UUID

from fastapi import APIRouter, Depends

from app.auth import Principal, require_aftersales
from app.client_api import get_client_state
from app.client_state import ClientState
from app.errors import ApiError
from app.match_analysis import Interval, Utterance, Window, analyze_match
from app.match_evidence import ChatClassifier
from app.schemas import (
    MatchEvidenceRequest,
    MatchEvidenceResponse,
    SessionMatchEvidenceRequest,
    TranscriptPutRequest,
    TranscriptPutResponse,
)
from app.transcripts import load_transcript, save_transcript

router = APIRouter(prefix="/api/v1/aftersales", tags=["aftersales"])


def get_evidence_classifier() -> Callable[[Window], object]:
    try:
        return ChatClassifier.from_environment()
    except ValueError as error:
        raise ApiError(503, "ERR_MODEL_NOT_CONFIGURED", "证据判定模型未配置") from error


# Sync handler: FastAPI runs it in the threadpool, so blocking model calls don't stall the loop.
@router.post("/match-evidence", response_model=MatchEvidenceResponse)
def match_evidence(
    body: MatchEvidenceRequest,
    _: Principal = Depends(require_aftersales),
    classify: Callable[[Window], object] = Depends(get_evidence_classifier),
) -> dict:
    try:
        match = Interval(body.start_ms, body.end_ms)
        coverage = [Interval(part.start_ms, part.end_ms) for part in body.coverage]
        utterances = [
            Utterance(row.start_ms, row.end_ms, row.id, row.speaker, row.text)
            for row in body.utterances
        ]
        return analyze_match(match, coverage, utterances, classify)
    except ValueError as error:
        raise ApiError(422, "ERR_VALIDATION", "时间区间非法、发言为空或发言 ID 重复") from error

def _run_analysis(start_ms, end_ms, coverage, utterances, classify) -> dict:
    try:
        match = Interval(start_ms, end_ms)
        parts = [Interval(start, end) for start, end in coverage]
        rows = [Utterance(r["start_ms"], r["end_ms"], r["id"], r["speaker"], r["text"]) for r in utterances]
        return analyze_match(match, parts, rows, classify)
    except ValueError as error:
        raise ApiError(422, "ERR_VALIDATION", "invalid time range, empty text or duplicate ids") from error


@router.put("/sessions/{session_id}/transcript", response_model=TranscriptPutResponse)
def put_transcript(
    session_id: UUID,
    body: TranscriptPutRequest,
    principal: Principal = Depends(require_aftersales),
    state: ClientState = Depends(get_client_state),
) -> dict:
    sid = str(session_id)
    session = state.sessions.get(sid)
    if session is None or session.studio_id != principal.studio_id:
        raise ApiError(404, "ERR_NOT_FOUND", "session not found")
    ids = [row.id for row in body.utterances]
    if len(set(ids)) != len(ids):
        raise ApiError(422, "ERR_VALIDATION", "duplicate utterance ids")
    if any(row.end_ms <= row.start_ms for row in [*body.coverage, *body.utterances]):
        raise ApiError(422, "ERR_VALIDATION", "end_ms must be greater than start_ms")
    save_transcript(
        principal.studio_id,
        sid,
        [(part.start_ms, part.end_ms) for part in body.coverage],
        [(row.id, row.speaker, row.start_ms, row.end_ms, row.text) for row in body.utterances],
    )
    return {"session_id": sid, "utterance_count": len(body.utterances), "coverage_count": len(body.coverage)}


@router.post("/sessions/{session_id}/match-evidence", response_model=MatchEvidenceResponse)
def session_match_evidence(
    session_id: UUID,
    body: SessionMatchEvidenceRequest,
    principal: Principal = Depends(require_aftersales),
    classify: Callable[[Window], object] = Depends(get_evidence_classifier),
) -> dict:
    transcript = load_transcript(principal.studio_id, str(session_id))
    if transcript is None:
        raise ApiError(404, "ERR_NOT_FOUND", "session not found")
    return _run_analysis(body.start_ms, body.end_ms, transcript["coverage"], transcript["utterances"], classify)
