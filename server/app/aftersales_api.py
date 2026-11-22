from collections.abc import Callable

from fastapi import APIRouter, Depends

from app.auth import Principal, require_aftersales
from app.errors import ApiError
from app.match_analysis import Interval, Utterance, Window, analyze_match
from app.match_evidence import ChatClassifier
from app.schemas import MatchEvidenceRequest, MatchEvidenceResponse

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
