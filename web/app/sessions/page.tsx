"use client";
import { useEffect, useState } from "react";
import { listSessions, type SessionSummary } from "@/lib/api";

export default function SessionsPage() {
  const [rows, setRows] = useState<SessionSummary[] | null>(null);
  useEffect(() => { listSessions().then(setRows); }, []);
  return (
    <main>
      <h1>接单会话</h1>
      <p style={{ color: "#888" }}>占位数据，待 GET /api/v1/aftersales/sessions 实现后接入。</p>
      {rows === null ? <p>加载中…</p> : rows.length === 0 ? <p>暂无会话</p> : (
        <table cellPadding={6}>
          <thead><tr><th>会话</th><th>订单</th><th>开始时间</th><th>ASR 进度</th></tr></thead>
          <tbody>
            {rows.map((s) => (
              <tr key={s.session_id}>
                <td>{s.session_id.slice(0, 8)}</td>
                <td>{s.order_ref ?? "-"}</td>
                <td>{s.started_at}</td>
                <td>{Math.round(s.asr_progress * 100)}%</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}