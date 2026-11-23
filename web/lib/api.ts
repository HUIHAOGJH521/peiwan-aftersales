// Types mirror server/app/schemas.py
export const API_BASE =
  process.env.NEXT_PUBLIC_API_BASE ?? "http://127.0.0.1:8000";

export interface EvidenceInterval { start_ms: number; end_ms: number }
export interface EvidenceUtterance extends EvidenceInterval {
  id: number;
  speaker: "player" | "system";
  text: string;
}
export interface MatchEvidenceRequest {
  start_ms: number;
  end_ms: number;
  coverage: EvidenceInterval[];
  utterances: EvidenceUtterance[];
}
export interface EvidenceItem extends EvidenceUtterance {
  reason: string;
  target_evidence_ids: number[];
}
export interface EvidenceEvent extends EvidenceInterval { evidence: EvidenceItem[] }
export interface EvidenceReview extends EvidenceInterval { id: number; reason: string }
export interface MatchEvidenceResponse {
  status: "analyzed" | "needs_review";
  candidate_count: number;
  events: EvidenceEvent[];
  review: EvidenceReview[];
  coverage_gaps: EvidenceInterval[];
  target_utterances: number;
  analyzed_utterances: number;
}
export interface ApiErrorBody { code: string; message: string }

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message);
  }
}

const TOKEN_KEY = "aftersales_jwt";
export const getToken = (): string | null =>
  typeof window === "undefined" ? null : window.sessionStorage.getItem(TOKEN_KEY);
export const setToken = (t: string) => window.sessionStorage.setItem(TOKEN_KEY, t);
export const clearToken = () => window.sessionStorage.removeItem(TOKEN_KEY);

/** Accepts {code,message} or FastAPI {detail:{code,message}} / {detail:string}. */
export function parseErrorBody(status: number, body: unknown): ApiError {
  const b = body as Record<string, unknown> | null;
  const pick = (o: unknown) =>
    o && typeof o === "object" && typeof (o as ApiErrorBody).code === "string"
      ? (o as ApiErrorBody)
      : null;
  const e = pick(b) ?? pick(b?.detail);
  if (e) return new ApiError(status, e.code, String(e.message ?? ""));
  const msg = typeof b?.detail === "string" ? b.detail : `HTTP ${status}`;
  return new ApiError(status, "ERR_HTTP", msg);
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = getToken();
  let res: Response;
  try {
    res = await fetch(`${API_BASE}${path}`, {
      ...init,
      headers: {
        "Content-Type": "application/json",
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
    });
  } catch {
    throw new ApiError(0, "ERR_NETWORK", "无法连接服务器");
  }
  if (!res.ok) {
    let body: unknown = null;
    try { body = await res.json(); } catch { /* non-JSON */ }
    throw parseErrorBody(res.status, body);
  }
  return (await res.json()) as T;
}

export const matchEvidence = (req: MatchEvidenceRequest) =>
  request<MatchEvidenceResponse>("/api/v1/aftersales/match-evidence", {
    method: "POST",
    body: JSON.stringify(req),
  });

// Session list: placeholder until GET /api/v1/aftersales/sessions exists.
export interface SessionSummary {
  session_id: string;
  order_ref: string | null;
  started_at: string;
  asr_progress: number;
}
export async function listSessions(): Promise<SessionSummary[]> {
  return [
    { session_id: "00000000-0000-0000-0000-000000000001", order_ref: "DEMO-001", started_at: "2025-01-01T20:00:00Z", asr_progress: 1 },
    { session_id: "00000000-0000-0000-0000-000000000002", order_ref: "DEMO-002", started_at: "2025-01-02T21:30:00Z", asr_progress: 0.4 },
  ];
}