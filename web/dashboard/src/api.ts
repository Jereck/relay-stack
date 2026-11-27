// Thin client for the RelayStack v1 API. The API key lives only in sessionStorage (cleared when the tab closes).

export type Connection = { baseUrl: string; apiKey: string };

const STORAGE_KEY = "relaystack.connection";

export function loadConnection(): Connection | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as Connection) : null;
  } catch {
    return null;
  }
}

export function saveConnection(c: Connection | null) {
  try {
    if (c) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(c));
    else sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    /* storage unavailable: the connection lasts for this page view only */
  }
}

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message);
  }
}

export async function api<T>(conn: Connection, path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(conn.baseUrl.replace(/\/$/, "") + path, {
    ...init,
    headers: { Authorization: `Bearer ${conn.apiKey}`, ...(init.headers ?? {}) },
  });
  const text = await res.text();
  const body = text ? JSON.parse(text) : null;
  if (!res.ok) {
    throw new ApiError(res.status, body?.code ?? "error", body?.detail ?? `Request failed with status ${res.status}.`);
  }
  return body as T;
}

export type List<T> = { data: T[]; has_more: boolean; next_cursor: string | null };

export type Project = {
  organization: { id: string; name: string };
  project: { id: string; name: string };
  environment: { id: string; kind: string };
  api_key: { id: string; scopes: string[] };
};

export type Inbox = {
  id: string;
  name: string;
  destination_id: string | null;
  schema: { columns: { name: string; type: string; required?: boolean }[] };
  max_file_bytes: number;
  retention_days: number;
  created_at: string;
};

export type FileItem = {
  id: string;
  inbox_id: string;
  original_name: string;
  size_bytes: number;
  sha256: string;
  status: "received" | "processing" | "completed" | "failed" | "expired";
  total_rows: number | null;
  valid_rows: number | null;
  invalid_rows: number | null;
  error_code: string | null;
  error_message: string | null;
  received_at: string;
  completed_at: string | null;
};

export type ValidationError = { row_number: number; column: string | null; code: string; message: string };

export type EventItem = {
  id: string;
  type: string;
  source_kind: string;
  source_id: string;
  record_count: number;
  body_bytes: number;
  created_at: string;
  expires_at: string;
  purged: boolean;
  payload: unknown;
};

export type Delivery = {
  id: string;
  event_id: string;
  status: "pending" | "in_flight" | "succeeded" | "failed" | "cancelled";
  attempt_count: number;
  max_attempts: number;
  next_attempt_at: string;
  last_status_code: number | null;
  last_error: string | null;
  replay_of: string | null;
  created_at: string;
};

export type Attempt = {
  attempt_number: number;
  started_at: string;
  duration_ms: number;
  outcome: string;
  status_code: number | null;
  error: string | null;
  request_bytes: number;
};

export type Usage = { totals: Record<string, number>; ratios: Record<string, number | null> };
