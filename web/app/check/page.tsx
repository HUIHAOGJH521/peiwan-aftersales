"use client";
import { useState } from "react";
import { ApiError, matchEvidence, type MatchEvidenceResponse } from "@/lib/api";
import { toResultRows } from "@/lib/format";

const SAMPLE = JSON.stringify(
  { coverage: [{ start_ms: 0, end_ms: 300000 }], utterances: [] }, null, 2);

type State =
  | { s: "idle" }
  | { s: "loading" }
  | { s: "error"; code: string; message: string }
  | { s: "done"; data: MatchEvidenceResponse };

export default function CheckPage() {
  const [start, setStart] = useState("0");
  const [end, setEnd] = useState("300000");
  const [json, setJson] = useState(SAMPLE);
  const [state, setState] = useState<State>({ s: "idle" });

  async function submit() {
    let body: { coverage?: unknown; utterances?: unknown };
    try { body = JSON.parse(json); } catch {
      return setState({ s: "error", code: "ERR_INPUT", message: "转写 JSON 格式不正确" });
    }
    const start_ms = Number(start), end_ms = Number(end);
    if (!Number.isInteger(start_ms) || !Number.isInteger(end_ms) || start_ms < 0 || end_ms <= start_ms) {
      return setState({ s: "error", code: "ERR_INPUT", message: "start_ms/end_ms 须为整数且 end_ms > start_ms" });
    }
    setState({ s: "loading" });
    try {
      const data = await matchEvidence({
        start_ms, end_ms,
        coverage: (body.coverage ?? []) as never,
        utterances: (body.utterances ?? []) as never,
      });
      setState({ s: "done", data });
    } catch (e) {
      const err = e instanceof ApiError ? e : new ApiError(0, "ERR_UNKNOWN", String(e));
      setState({ s: "error", code: err.code, message: err.message });
    }
  }

  const rows = state.s === "done" ? toResultRows(state.data) : [];
  return (
    <main>
      <h1>单局核对</h1>
      <label>start_ms <input value={start} onChange={(e) => setStart(e.target.value)} /></label>{" "}
      <label>end_ms <input value={end} onChange={(e) => setEnd(e.target.value)} /></label>
      <p>转写 JSON（含 coverage 与 utterances）：</p>
      <textarea rows={12} style={{ width: "100%", fontFamily: "monospace" }} value={json} onChange={(e) => setJson(e.target.value)} />
      <button onClick={submit} disabled={state.s === "loading"}>开始核对</button>

      {state.s === "loading" && <p>分析中…</p>}
      {state.s === "error" && <p role="alert" style={{ color: "crimson" }}>错误 {state.code}：{state.message}</p>}
      {state.s === "done" && (
        <section>
          <p>状态：{state.data.status}；候选 {state.data.candidate_count}；分析发言 {state.data.analyzed_utterances}/{state.data.target_utterances}</p>
          {rows.length === 0 ? <p>无结果</p> : rows.map((r, i) => (
            <div key={i} style={{ border: "1px solid #ccc", padding: 8, margin: "8px 0",
              borderColor: r.kind === "gap" ? "orange" : r.kind === "review" ? "goldenrod" : "seagreen" }}>
              <strong>{r.title}</strong> <span>{r.range}</span>
              {r.detail.map((d, j) => <div key={j}>{d}</div>)}
            </div>
          ))}
        </section>
      )}
    </main>
  );
}