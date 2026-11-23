import type { EvidenceInterval, MatchEvidenceResponse } from "./api";

/** 毫秒 -> mm:ss (>=1h 时 h:mm:ss). 负数/非法值按 0。 */
export function formatMs(ms: number): string {
  const total = Number.isFinite(ms) && ms > 0 ? Math.floor(ms / 1000) : 0;
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const p = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${p(m)}:${p(s)}` : `${p(m)}:${p(s)}`;
}

export const formatRange = (i: EvidenceInterval) =>
  `${formatMs(i.start_ms)} - ${formatMs(i.end_ms)}`;

export interface ResultRow {
  kind: "event" | "review" | "gap";
  range: string;
  title: string;
  detail: string[];
}

const SPEAKER = { player: "老板", system: "陪玩" } as const;

export function toResultRows(r: MatchEvidenceResponse): ResultRow[] {
  const rows: ResultRow[] = [];
  r.events.forEach((e, i) =>
    rows.push({
      kind: "event",
      range: formatRange(e),
      title: `候选事件 ${i + 1}`,
      detail: e.evidence.map(
        (x) => `[${formatMs(x.start_ms)}] ${SPEAKER[x.speaker]}: ${x.text}（${x.reason}）`,
      ),
    }),
  );
  r.review.forEach((v) =>
    rows.push({ kind: "review", range: formatRange(v), title: `待复核发言 #${v.id}`, detail: [v.reason] }),
  );
  r.coverage_gaps.forEach((g) =>
    rows.push({ kind: "gap", range: formatRange(g), title: "覆盖缺口", detail: [] }),
  );
  return rows;
}