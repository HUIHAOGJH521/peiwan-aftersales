"""Local demo: fixture transcripts plus keyword evidence scan. Not the production model."""

from __future__ import annotations

import json
from functools import lru_cache
from pathlib import Path

from fastapi import APIRouter
from fastapi.responses import FileResponse

from pydantic import BaseModel, Field

from app.errors import ApiError
from app.match_analysis import Interval, Utterance, Window, analyze_match
from app.records import list_reviews, list_verdicts, records_db_path, save_review, save_verdict

router = APIRouter(tags=["demo"])
FIXTURES = Path(__file__).resolve().parents[1] / "tests" / "fixtures" / "social"
PAGE = Path(__file__).resolve().parent / "static" / "demo.html"

SAMPLES = (
    ("court", "court_dispute_transcript.json", "开庭念投诉", "售后主持逐条念老板投诉"),
    ("peiwan", "peiwan_complaint_transcript.json", "陪玩投诉店长", "对赌人头、老板读书"),
    ("escort", "delta_escort_transcript.json", "三角洲护航局内", "求放过、感谢哥，没有辱骂"),
)


def keyword_classify(window: Window) -> dict:
    decisions = []
    for target in window.targets:
        text = next(item.text for item in window.context if item.id == target)
        if any(word in text for word in ("骂", "罵", "脑瘫", "腦殘", "傻逼", "滾", "滚")):
            label, reason = "candidate", "命中辱骂词，待人工确认"
        elif any(word in text for word in ("投诉", "投訴", "对赌", "對賭", "人头", "人頭", "押金", "曝光", "证据不足", "證據不足")):
            label, reason = "review", "纠纷用语，待人工复核"
        else:
            label, reason = "excluded", "未发现辱骂或纠纷用语"
        decisions.append({
            "id": target,
            "label": label,
            "reason": reason,
            "target_evidence_ids": [target],
            "continues_from": None,
        })
    return {"decisions": decisions}


@lru_cache(maxsize=8)
def _load(name: str) -> dict:
    path = FIXTURES / name
    if not path.is_file():
        raise FileNotFoundError(name)
    return json.loads(path.read_text(encoding="utf-8-sig"))


def _sample(sample_id: str) -> tuple[str, str, str, dict]:
    for item in SAMPLES:
        if item[0] == sample_id:
            try:
                return item[0], item[2], item[3], _load(item[1])
            except FileNotFoundError as error:
                raise ApiError(404, "ERR_NOT_FOUND", "样本转写不存在") from error
    raise ApiError(404, "ERR_NOT_FOUND", "没有这个样本")


@router.get("/")
@router.get("/demo")
def demo_page() -> FileResponse:
    return FileResponse(PAGE)


@router.get("/api/v1/demo/samples")
def list_samples() -> dict:
    items = []
    for sample_id, filename, title, summary in SAMPLES:
        try:
            payload = _load(filename)
        except FileNotFoundError:
            continue
        items.append({
            "id": sample_id,
            "title": title,
            "summary": summary,
            "utterance_count": len(payload.get("utterances", [])),
        })
    return {"samples": items}


@router.get("/api/v1/demo/samples/{sample_id}")
def get_sample(sample_id: str) -> dict:
    sample_id, title, summary, payload = _sample(sample_id)
    return {
        "id": sample_id,
        "title": title,
        "summary": summary,
        "start_ms": payload["start_ms"],
        "end_ms": payload["end_ms"],
        "utterances": payload["utterances"],
    }


@router.post("/api/v1/demo/samples/{sample_id}/analyze")
def analyze_sample(sample_id: str) -> dict:
    sample_id, title, _, payload = _sample(sample_id)
    try:
        result = analyze_match(
            Interval(payload["start_ms"], payload["end_ms"]),
            [Interval(part["start_ms"], part["end_ms"]) for part in payload["coverage"]],
            [Utterance(row["start_ms"], row["end_ms"], row["id"], row["speaker"], row["text"])
             for row in payload["utterances"]],
            keyword_classify,
        )
    except ValueError as error:
        raise ApiError(422, "ERR_VALIDATION", "样本转写无法分析") from error
    record = save_review(sample_id, title, result)
    return {"id": sample_id, "title": title, "analysis": result, "record": record}


@router.get("/api/v1/demo/records")
def demo_records() -> dict:
    return {"records": list_reviews(), "database": str(records_db_path())}


class VerdictRequest(BaseModel):
    utterance_id: int = Field(ge=1)
    start_ms: int = Field(ge=0)
    text: str = Field(min_length=1, max_length=2000)
    verdict: str = Field(pattern="^(成立|不成立|再看)$")
    note: str = Field(default="", max_length=200)


@router.get("/api/v1/demo/verdicts")
def demo_verdicts(sample_id: str = "") -> dict:
    return {"verdicts": list_verdicts(sample_id or None)}


@router.post("/api/v1/demo/samples/{sample_id}/verdicts")
def demo_save_verdict(sample_id: str, body: VerdictRequest) -> dict:
    _sample(sample_id)
    try:
        saved = save_verdict(
            sample_id, body.utterance_id, body.start_ms, body.text, body.verdict, body.note,
        )
    except ValueError as error:
        raise ApiError(422, "ERR_VALIDATION", "结论只能是成立、不成立或再看") from error
    return saved
