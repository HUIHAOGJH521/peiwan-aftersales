import { describe, expect, it } from "vitest";
import { formatMs, formatRange, toResultRows } from "./format";
import type { MatchEvidenceResponse } from "./api";

describe("formatMs", () => {
  it("formats mm:ss", () => {
    expect(formatMs(0)).toBe("00:00");
    expect(formatMs(65_999)).toBe("01:05");
    expect(formatMs(1_420_000)).toBe("23:40");
  });
  it("formats hours", () => expect(formatMs(3_661_000)).toBe("1:01:01"));
  it("clamps invalid", () => {
    expect(formatMs(-5)).toBe("00:00");
    expect(formatMs(NaN)).toBe("00:00");
  });
  it("formats range", () => expect(formatRange({ start_ms: 0, end_ms: 90_000 })).toBe("00:00 - 01:30"));
});

describe("toResultRows", () => {
  const base: MatchEvidenceResponse = {
    status: "needs_review", candidate_count: 1, target_utterances: 2, analyzed_utterances: 2,
    events: [{ start_ms: 10_000, end_ms: 40_000, evidence: [
      { id: 1, speaker: "player", start_ms: 12_000, end_ms: 13_000, text: "你怎么不报点", reason: "抱怨", target_evidence_ids: [] },
    ] }],
    review: [{ id: 2, start_ms: 50_000, end_ms: 51_000, reason: "模型失败" }],
    coverage_gaps: [{ start_ms: 60_000, end_ms: 70_000 }],
  };
  it("maps all kinds in order", () => {
    const rows = toResultRows(base);
    expect(rows.map((r) => r.kind)).toEqual(["event", "review", "gap"]);
    expect(rows[0].range).toBe("00:10 - 00:40");
    expect(rows[0].detail[0]).toBe("[00:12] 老板: 你怎么不报点（抱怨）");
    expect(rows[1].title).toBe("待复核发言 #2");
    expect(rows[2].range).toBe("01:00 - 01:10");
  });
  it("empty result gives no rows", () => {
    expect(toResultRows({ ...base, events: [], review: [], coverage_gaps: [] })).toEqual([]);
  });
});