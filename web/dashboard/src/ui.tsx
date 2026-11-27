import { useCallback, useEffect, useState, type ReactNode } from "react";
import { api, ApiError, type Connection } from "./api";

export function useApi<T>(conn: Connection, path: string | null, refreshMs?: number) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  const reload = useCallback(() => setTick((t) => t + 1), []);

  useEffect(() => {
    if (!path) return;
    let cancelled = false;
    api<T>(conn, path)
      .then((d) => !cancelled && (setData(d), setError(null)))
      .catch((e: unknown) => !cancelled && setError(e instanceof ApiError ? e.message : "The API could not be reached. Check the API URL and that the API is running."));
    return () => {
      cancelled = true;
    };
  }, [conn, path, tick]);

  useEffect(() => {
    if (!refreshMs) return;
    const id = setInterval(reload, refreshMs);
    return () => clearInterval(id);
  }, [refreshMs, reload]);

  return { data, error, reload };
}

export const bytes = (n: number | null | undefined) =>
  n == null ? "—" : n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`;

export const when = (iso: string | null | undefined) =>
  iso ? new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "medium" }) : "—";

export const num = (n: number | null | undefined) => (n == null ? "—" : n.toLocaleString());

/** Ids are real data people copy into curl and logs, so they get the monospace face and a copy affordance. */
export function Id({ value, short = true }: { value: string; short?: boolean }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      title={copied ? "Copied" : `Copy ${value}`}
      onClick={(e) => {
        e.stopPropagation();
        navigator.clipboard?.writeText(value).then(() => {
          setCopied(true);
          setTimeout(() => setCopied(false), 1200);
        });
      }}
      className="font-mono text-[13px] text-muted hover:text-ink"
    >
      {short ? `${value.slice(0, 8)}…${value.slice(-4)}` : value}
      <span className="sr-only">{copied ? " copied" : " copy"}</span>
    </button>
  );
}

const statusStyle: Record<string, string> = {
  succeeded: "bg-ok-soft text-ok",
  completed: "bg-ok-soft text-ok",
  pending: "bg-wait-soft text-wait",
  in_flight: "bg-wait-soft text-wait",
  received: "bg-wait-soft text-wait",
  processing: "bg-wait-soft text-wait",
  failed: "bg-bad-soft text-bad",
  cancelled: "bg-page text-muted",
  expired: "bg-page text-muted",
};

const statusLabel: Record<string, string> = { in_flight: "sending", failed: "failed" };

export function Status({ value }: { value: string }) {
  return (
    <span className={`inline-flex items-center gap-1.5 rounded px-2 py-0.5 text-[13px] font-medium ${statusStyle[value] ?? "bg-page text-muted"}`}>
      <span aria-hidden className="h-1.5 w-1.5 rounded-full bg-current" />
      {statusLabel[value] ?? value}
    </span>
  );
}

export function Page({ title, aside, children }: { title: ReactNode; aside?: ReactNode; children: ReactNode }) {
  return (
    <div className="mx-auto max-w-[1080px] px-6 py-8 lg:px-10">
      <div className="mb-6 flex flex-wrap items-end justify-between gap-4">
        <h1 className="text-[26px] font-semibold leading-tight tracking-[-0.01em]">{title}</h1>
        {aside}
      </div>
      <div className="space-y-8">{children}</div>
    </div>
  );
}

export function Section({ title, aside, children }: { title: string; aside?: ReactNode; children: ReactNode }) {
  return (
    <section>
      <div className="mb-3 flex items-baseline justify-between gap-4">
        <h2 className="text-[17px] font-semibold">{title}</h2>
        {aside}
      </div>
      {children}
    </section>
  );
}

export function Table({ head, children, empty }: { head: string[]; children: ReactNode; empty?: ReactNode }) {
  const rows = Array.isArray(children) ? children.flat().filter(Boolean) : children ? [children] : [];
  return (
    <div className="overflow-x-auto rounded-md border border-line bg-surface">
      <table className="w-full border-collapse text-left text-[14px]">
        <thead>
          <tr className="border-b border-line text-muted">
            {head.map((h) => (
              <th key={h} scope="col" className="whitespace-nowrap px-4 py-2.5 font-medium">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-line">{children}</tbody>
      </table>
      {rows.length === 0 && empty && <div className="px-4 py-8 text-muted">{empty}</div>}
    </div>
  );
}

export function Notice({ tone = "bad", children }: { tone?: "bad" | "ok"; children: ReactNode }) {
  return (
    <div role={tone === "bad" ? "alert" : "status"} className={`rounded-md px-4 py-3 text-[14px] ${tone === "bad" ? "bg-bad-soft text-bad" : "bg-ok-soft text-ok"}`}>
      {children}
    </div>
  );
}

export function Button({ children, onClick, disabled, variant = "primary", type = "button" }: {
  children: ReactNode;
  onClick?: () => void;
  disabled?: boolean;
  variant?: "primary" | "quiet";
  type?: "button" | "submit";
}) {
  const base = "rounded-md px-3.5 py-1.5 text-[14px] font-medium disabled:cursor-not-allowed disabled:opacity-50";
  const style = variant === "primary" ? "bg-cobalt text-white hover:bg-[#1d3aaf]" : "border border-line bg-surface text-ink hover:border-muted";
  return (
    <button type={type} onClick={onClick} disabled={disabled} className={`${base} ${style}`}>
      {children}
    </button>
  );
}

export const link = "text-cobalt hover:underline underline-offset-2";
