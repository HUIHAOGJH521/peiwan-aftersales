from bisect import bisect_right
from collections.abc import Callable, Iterator
from dataclasses import asdict, dataclass


@dataclass(frozen=True)
class Interval:
    start_ms: int
    end_ms: int

    def __post_init__(self) -> None:
        if (
            type(self.start_ms) is not int or type(self.end_ms) is not int
            or not 0 <= self.start_ms < self.end_ms
        ):
            raise ValueError("Invalid time interval")


@dataclass(frozen=True)
class Utterance(Interval):
    id: int
    speaker: str
    text: str

    def __post_init__(self) -> None:
        super().__post_init__()
        if type(self.id) is not int or self.id < 1:
            raise ValueError("Invalid utterance ID")
        if self.speaker not in ("player", "system"):
            raise ValueError("Invalid utterance speaker")
        if not isinstance(self.text, str) or not self.text.strip():
            raise ValueError("Empty utterance text")


@dataclass(frozen=True)
class Window:
    context: tuple[Utterance, ...]
    targets: tuple[int, ...]


class ClassificationUnavailable(Exception):
    """The classifier could not return a usable batch."""


def _merge_coverage(match: Interval, coverage: list[Interval]) -> list[Interval]:
    merged: list[Interval] = []
    for part in sorted(coverage, key=lambda part: part.start_ms):
        start = max(part.start_ms, match.start_ms)
        end = min(part.end_ms, match.end_ms)
        if start >= end:
            continue
        if merged and start <= merged[-1].end_ms:
            previous = merged.pop()
            merged.append(Interval(previous.start_ms, max(previous.end_ms, end)))
        else:
            merged.append(Interval(start, end))
    return merged


def _windows(items: list[Utterance]) -> Iterator[Window]:
    start = 0
    while start < len(items):
        stop = start + 1
        first_ms = items[start].start_ms
        last_ms = items[start].end_ms
        natural_boundary = None
        while stop < len(items) and stop - start < 80:
            following = items[stop]
            if max(last_ms, following.end_ms) - first_ms > 30_000:
                break
            if last_ms - first_ms >= 15_000 and following.start_ms - last_ms >= 1_000:
                natural_boundary = stop
            last_ms = max(last_ms, following.end_ms)
            stop += 1
        if stop < len(items) and natural_boundary is not None:
            stop = natural_boundary
        core = items[start:stop]
        left_ms, right_ms = core[0].start_ms, max(item.end_ms for item in core)
        before = tuple(item for item in items[max(0, start - 2):start]
                       if item.start_ms >= left_ms - 10_000)
        after = tuple(item for item in items[stop:stop + 2]
                      if item.end_ms <= right_ms + 10_000)
        targets = tuple(item.id for item in core if item.speaker == "player")
        if targets:
            yield Window(before + tuple(core) + after, targets)
        start = stop


def _validate_decisions(window: Window, response: object) -> dict[int, dict]:
    if not isinstance(response, dict) or not isinstance(response.get("decisions"), list):
        return {}
    targets = set(window.targets)
    visible = {item.id for item in window.context}
    decisions: dict[int, dict] = {}
    seen: set[int] = set()
    for row in response["decisions"]:
        if not isinstance(row, dict):
            continue
        identifier = row.get("id")
        if type(identifier) is not int or identifier not in targets:
            continue
        if identifier in seen:
            decisions.pop(identifier, None)
            continue
        seen.add(identifier)
        label, reason = row.get("label"), row.get("reason")
        link = row.get("continues_from")
        if (
            label not in ("excluded", "candidate", "review")
            or not isinstance(reason, str) or not reason.strip()
            or (link is not None and (type(link) is not int or label != "candidate"))
        ):
            continue
        if label == "candidate":
            sources = row.get("target_evidence_ids")
            if "continues_from" not in row or not isinstance(sources, list) or not sources or any(
                type(source) is not int or source not in visible for source in sources
            ):
                continue
        decisions[identifier] = row
    return decisions


def analyze_match(
    match: Interval,
    coverage: list[Interval],
    utterances: list[Utterance],
    classify: Callable[[Window], object],
) -> dict:
    if len({item.id for item in utterances}) != len(utterances):
        raise ValueError("Duplicate utterance IDs")
    runs = _merge_coverage(match, coverage)
    starts = [run.start_ms for run in runs]
    groups: list[list[Utterance]] = [[] for _ in runs]
    reviews: list[dict] = []
    events: list[dict] = []
    gaps: list[Interval] = []
    cursor = match.start_ms
    for run in runs:
        if cursor < run.start_ms:
            gaps.append(Interval(cursor, run.start_ms))
        cursor = run.end_ms
    if cursor < match.end_ms:
        gaps.append(Interval(cursor, match.end_ms))

    targets = 0
    analyzed = 0
    for item in sorted(utterances, key=lambda item: (item.start_ms, item.id)):
        if item.end_ms <= match.start_ms or item.start_ms >= match.end_ms:
            continue
        targets += item.speaker == "player"
        index = bisect_right(starts, item.start_ms) - 1
        if index >= 0 and item.end_ms <= runs[index].end_ms:
            groups[index].append(item)
        elif item.speaker == "player":
            reviews.append({
                "id": item.id, "start_ms": max(item.start_ms, match.start_ms),
                "end_ms": min(item.end_ms, match.end_ms), "reason": "发言跨越本局边界或覆盖缺口",
            })

    for group in groups:
        previous_candidate = None
        for window in _windows(group):
            try:
                decisions = _validate_decisions(window, classify(window))
            except ClassificationUnavailable:
                decisions = {}
            visible = {item.id: item for item in window.context}
            for identifier in window.targets:
                item = visible[identifier]
                row = decisions.get(identifier)
                reason = row["reason"] if row else "判定器未返回有效结果"
                if row and row["label"] == "excluded":
                    analyzed += 1
                    continue
                if row and row["label"] == "candidate":
                    link = row.get("continues_from")
                    if link is None or (link in visible and link == previous_candidate):
                        evidence = {
                            **asdict(item), "reason": reason,
                            "target_evidence_ids": list(dict.fromkeys(row["target_evidence_ids"])),
                        }
                        if link is None:
                            events.append({"start_ms": item.start_ms, "end_ms": item.end_ms, "evidence": []})
                        event = events[-1]
                        event["end_ms"] = max(event["end_ms"], item.end_ms)
                        event["evidence"].append(evidence)
                        previous_candidate = item.id
                        analyzed += 1
                        continue
                    reason = "判定器给出了无效的事件合并引用"
                if row and row["label"] == "review":
                    analyzed += 1
                reviews.append({
                    "id": identifier, "start_ms": item.start_ms,
                    "end_ms": item.end_ms, "reason": reason,
                })
    return {
        "candidate_count": len(events),
        "events": events,
        "review": sorted(reviews, key=lambda row: (row["start_ms"], row["id"])),
        "coverage_gaps": [asdict(gap) for gap in gaps],
        "target_utterances": targets,
        "analyzed_utterances": analyzed,
        "status": "needs_review" if gaps or reviews else "analyzed",
    }
