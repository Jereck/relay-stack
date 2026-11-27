import { useRef, useState } from "react";
import { api, ApiError, type Attempt, type Connection, type Delivery, type EventItem, type FileItem, type Inbox, type List, type Project, type Usage, type ValidationError } from "./api";
import { Button, bytes, Id, link, Notice, num, Page, Section, Status, Table, useApi, when } from "./ui";

type P = { conn: Connection };

// ---- Overview --------------------------------------------------------------------------------------

const metricLabels: Record<string, string> = {
  files_uploaded: "Files uploaded",
  bytes_ingested: "Bytes received",
  records_processed: "Rows processed",
  records_invalid: "Rows rejected",
  events_created: "Events created",
  event_bytes_created: "Event bytes created",
  delivery_attempts: "Delivery attempts",
  delivery_bytes_sent: "Bytes sent (all attempts)",
  delivery_bytes_delivered: "Bytes delivered",
  storage_byte_days_committed: "Storage committed (byte-days)",
  processing_ms: "Processing time (ms)",
  poll_requests: "Poll requests",
};

const byteMetrics = new Set(["bytes_ingested", "event_bytes_created", "delivery_bytes_sent", "delivery_bytes_delivered"]);

export function Overview({ conn }: P) {
  const project = useApi<Project>(conn, "/v1/project");
  const usage = useApi<Usage>(conn, "/v1/usage", 10000);
  const ratios = usage.data?.ratios;
  return (
    <Page title={project.data ? project.data.project.name : "Project"} aside={project.data && <EnvBadge kind={project.data.environment.kind} />}>
      {project.error && <Notice>{project.error}</Notice>}
      {project.data && (
        <p className="max-w-[70ch] text-muted">
          {project.data.organization.name}. You are connected to the {project.data.environment.kind} environment with a key that has{" "}
          {project.data.api_key.scopes.length} of 12 permissions.
        </p>
      )}
      <Section title="Usage, last 30 days">
        {usage.error && <Notice>{usage.error}</Notice>}
        {usage.data && (
          <Table head={["Measure", "Total"]}>
            {Object.entries(metricLabels).map(([k, label]) => (
              <tr key={k}>
                <td className="px-4 py-2.5">{label}</td>
                <td className="px-4 py-2.5 tabular-nums">{byteMetrics.has(k) ? bytes(usage.data!.totals[k]) : num(usage.data!.totals[k])}</td>
              </tr>
            ))}
          </Table>
        )}
      </Section>
      {ratios && (
        <Section title="How much work each byte creates">
          <dl className="grid gap-px overflow-hidden rounded-md border border-line bg-line sm:grid-cols-3">
            <Ratio label="Bytes sent per byte received" value={ratios.delivery_bytes_sent_per_byte_ingested} hint="Includes JSON expansion and every retry." />
            <Ratio label="Attempts per event" value={ratios.delivery_attempts_per_event} hint="1.0 means every webhook succeeded first time." />
            <Ratio label="Retry overhead" value={ratios.retry_overhead_bytes_ratio} hint="Extra bytes sent by failed attempts, relative to delivered bytes." />
          </dl>
        </Section>
      )}
    </Page>
  );
}

function Ratio({ label, value, hint }: { label: string; value: number | null | undefined; hint: string }) {
  return (
    <div className="bg-surface px-4 py-4">
      <dt className="text-[14px] text-muted">{label}</dt>
      <dd className="mt-1 text-[28px] font-semibold tabular-nums">{value == null ? "—" : `${value.toFixed(2)}×`}</dd>
      <dd className="mt-1 text-[13px] text-muted">{hint}</dd>
    </div>
  );
}

export function EnvBadge({ kind }: { kind: string }) {
  return (
    <span className={`rounded px-2 py-0.5 text-[13px] font-medium ${kind === "production" ? "bg-bad-soft text-bad" : "bg-cobalt-soft text-cobalt"}`}>
      {kind === "production" ? "Production" : "Development"}
    </span>
  );
}

// ---- Inboxes ---------------------------------------------------------------------------------------

export function Inboxes({ conn }: P) {
  const { data, error } = useApi<List<Inbox>>(conn, "/v1/inboxes?limit=100");
  return (
    <Page title="Inboxes">
      {error && <Notice>{error}</Notice>}
      <Table head={["Name", "Columns", "Webhook", "Keeps files for", "Created", "Id"]}
        empty={<>No inboxes yet. Create one with <code className="font-mono text-ink">POST /v1/inboxes</code>; see the curl walkthrough in the repository.</>}>
        {data?.data.map((i) => (
          <tr key={i.id} className="hover:bg-page/60">
            <td className="px-4 py-2.5"><a className={link} href={`#/inboxes/${i.id}`}>{i.name}</a></td>
            <td className="px-4 py-2.5 tabular-nums">{i.schema.columns.length}</td>
            <td className="px-4 py-2.5">{i.destination_id ? "Configured" : <span className="text-muted">None</span>}</td>
            <td className="px-4 py-2.5">{i.retention_days} days</td>
            <td className="px-4 py-2.5 text-muted">{when(i.created_at)}</td>
            <td className="px-4 py-2.5"><Id value={i.id} /></td>
          </tr>
        ))}
      </Table>
    </Page>
  );
}

export function InboxDetail({ conn, id }: P & { id: string }) {
  const inbox = useApi<Inbox>(conn, `/v1/inboxes/${id}`);
  const files = useApi<List<FileItem>>(conn, `/v1/inboxes/${id}/files?limit=50`, 3000);
  return (
    <Page title={inbox.data?.name ?? "Inbox"} aside={<Id value={id} short={false} />}>
      {inbox.error && <Notice>{inbox.error}</Notice>}
      {inbox.data && <Upload conn={conn} inbox={inbox.data} onUploaded={files.reload} />}
      <Section title="Files">
        <Table head={["File", "Status", "Rows", "Rejected", "Size", "Received"]} empty="No files have been uploaded to this inbox.">
          {files.data?.data.map((f) => (
            <tr key={f.id} className="hover:bg-page/60">
              <td className="px-4 py-2.5"><a className={link} href={`#/files/${f.id}`}>{f.original_name}</a></td>
              <td className="px-4 py-2.5"><Status value={f.status} /></td>
              <td className="px-4 py-2.5 tabular-nums">{num(f.total_rows)}</td>
              <td className={`px-4 py-2.5 tabular-nums ${f.invalid_rows ? "text-bad" : ""}`}>{num(f.invalid_rows)}</td>
              <td className="px-4 py-2.5 tabular-nums">{bytes(f.size_bytes)}</td>
              <td className="px-4 py-2.5 text-muted">{when(f.received_at)}</td>
            </tr>
          ))}
        </Table>
      </Section>
      {inbox.data && (
        <Section title="Expected columns">
          <p className="text-[14px] text-muted">
            {inbox.data.schema.columns.map((c, i) => (
              <span key={c.name}>
                {i > 0 && ", "}
                <code className="font-mono text-ink">{c.name}</code> {c.type}{c.required ? " (required)" : ""}
              </span>
            ))}
          </p>
        </Section>
      )}
    </Page>
  );
}

function Upload({ conn, inbox, onUploaded }: P & { inbox: Inbox; onUploaded: () => void }) {
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "bad"; text: string; fileId?: string } | null>(null);
  const [dragging, setDragging] = useState(false);

  async function send(file: File) {
    setBusy(true);
    setMessage(null);
    try {
      const res = await api<FileItem>(conn, `/v1/inboxes/${inbox.id}/files?filename=${encodeURIComponent(file.name)}`, {
        method: "POST",
        headers: { "Content-Type": "text/csv" },
        body: file,
      });
      setMessage({ tone: "ok", text: `Uploaded ${res.original_name}. Processing has started.`, fileId: res.id });
      onUploaded();
    } catch (e) {
      setMessage({ tone: "bad", text: e instanceof ApiError ? e.message : "Upload failed. Check that the API is running." });
    } finally {
      setBusy(false);
      if (input.current) input.current.value = "";
    }
  }

  return (
    <div
      onDragOver={(e) => (e.preventDefault(), setDragging(true))}
      onDragLeave={() => setDragging(false)}
      onDrop={(e) => {
        e.preventDefault();
        setDragging(false);
        const f = e.dataTransfer.files[0];
        if (f) void send(f);
      }}
      className={`rounded-md border border-dashed px-5 py-5 ${dragging ? "border-cobalt bg-cobalt-soft" : "border-line bg-surface"}`}
    >
      <div className="flex flex-wrap items-center gap-4">
        <Button onClick={() => input.current?.click()} disabled={busy}>{busy ? "Uploading…" : "Upload CSV"}</Button>
        <p className="text-[14px] text-muted">or drop a file here. UTF-8 CSV with a header row, up to {bytes(inbox.max_file_bytes)}.</p>
        <input ref={input} type="file" accept=".csv,text/csv" className="sr-only" onChange={(e) => e.target.files?.[0] && void send(e.target.files[0])} />
      </div>
      {message && (
        <div className="mt-4">
          <Notice tone={message.tone}>
            {message.text} {message.fileId && <a className="underline" href={`#/files/${message.fileId}`}>View the file</a>}
          </Notice>
        </div>
      )}
    </div>
  );
}

// ---- File detail: the pipeline trace ----------------------------------------------------------------

export function FileDetail({ conn, id }: P & { id: string }) {
  const file = useApi<FileItem>(conn, `/v1/files/${id}`, 2000);
  const errors = useApi<List<ValidationError>>(conn, `/v1/files/${id}/validation-errors?limit=200`, 5000);
  const events = useApi<List<EventItem>>(conn, `/v1/events?source_id=${id}&limit=100`, 3000);
  const f = file.data;
  return (
    <Page title={f?.original_name ?? "File"} aside={f && <Status value={f.status} />}>
      {file.error && <Notice>{file.error}</Notice>}
      {f && <Trace conn={conn} file={f} events={events.data?.data ?? []} />}
      {f?.error_code && (
        <Notice>
          <span className="font-medium">The file could not be processed ({f.error_code}).</span> {f.error_message} Fix the file and upload it again.
        </Notice>
      )}
      <Section title="Rejected rows" aside={f?.invalid_rows ? <span className="text-[14px] text-muted">Row numbers count data rows; the header is row 0.</span> : null}>
        <Table head={["Row", "Column", "Problem", "Code"]} empty={f?.status === "completed" ? "Every row passed validation." : "Rejected rows appear here once processing finishes."}>
          {errors.data?.data.map((e, i) => (
            <tr key={`${e.row_number}-${e.column}-${i}`}>
              <td className="px-4 py-2.5 tabular-nums">{e.row_number}</td>
              <td className="px-4 py-2.5 font-mono text-[13px]">{e.column ?? "whole row"}</td>
              <td className="px-4 py-2.5">{e.message}</td>
              <td className="px-4 py-2.5 font-mono text-[13px] text-muted">{e.code}</td>
            </tr>
          ))}
        </Table>
      </Section>
      <Section title="Events from this file">
        <EventTable events={events.data?.data ?? []} />
      </Section>
      {f && (
        <p className="text-[13px] text-muted">
          SHA-256 <span className="font-mono">{f.sha256}</span>. Received {when(f.received_at)}.
        </p>
      )}
    </Page>
  );
}

/** The five stages a file moves through, with what happened at each. */
function Trace({ conn, file, events }: P & { file: FileItem; events: EventItem[] }) {
  const all = useApi<List<Delivery>>(conn, "/v1/deliveries?limit=100", 2000);
  const ids = new Set(events.map((e) => e.id));
  const mine = all.data?.data.filter((d) => ids.has(d.event_id) && !d.replay_of) ?? [];
  const delivered = mine.filter((d) => d.status === "succeeded").length;
  const failed = mine.filter((d) => d.status === "failed").length;
  const done = file.status === "completed" || file.status === "failed";

  const stages: { name: string; value: string; detail: string; state: "done" | "active" | "bad" | "idle" }[] = [
    { name: "Received", value: bytes(file.size_bytes), detail: "stored and queued", state: "done" },
    { name: "Parsed", value: num(file.total_rows), detail: "rows read", state: file.status === "failed" ? "bad" : done ? "done" : "active" },
    { name: "Validated", value: num(file.valid_rows), detail: file.invalid_rows ? `${num(file.invalid_rows)} rejected` : "rows accepted", state: !done ? "idle" : file.invalid_rows ? "bad" : "done" },
    { name: "Events", value: num(events.length), detail: "signed payloads", state: events.length ? "done" : "idle" },
    {
      name: "Delivered",
      value: mine.length ? `${delivered}/${mine.length}` : "—",
      detail: failed ? `${failed} failed` : mine.length && delivered < mine.length ? "sending" : "webhooks",
      state: !mine.length ? "idle" : failed ? "bad" : delivered === mine.length ? "done" : "active",
    },
  ];
  const tone = { done: "bg-ok", active: "bg-wait", bad: "bg-bad", idle: "bg-line" } as const;

  return (
    <ol className="grid grid-cols-2 gap-y-6 rounded-md border border-line bg-surface px-5 py-6 sm:grid-cols-5" aria-label="Processing stages">
      {stages.map((s, i) => (
        <li key={s.name} className="relative pr-4">
          <div className="flex items-center">
            <span aria-hidden className={`h-3 w-3 shrink-0 rounded-full ${tone[s.state]}`} />
            {i < stages.length - 1 && <span aria-hidden className={`ml-2 hidden h-0.5 flex-1 sm:block ${s.state === "done" ? "bg-ok/50" : "bg-line"}`} />}
          </div>
          <div className="mt-3 text-[14px] text-muted">{s.name}</div>
          <div className="text-[24px] font-semibold leading-tight tabular-nums">{s.value}</div>
          <div className={`text-[13px] ${s.state === "bad" ? "text-bad" : "text-muted"}`}>{s.detail}</div>
        </li>
      ))}
    </ol>
  );
}

// ---- Events ----------------------------------------------------------------------------------------

function EventTable({ events }: { events: EventItem[] }) {
  return (
    <Table head={["Type", "Records", "Size", "Created", "Id"]} empty="No events yet.">
      {events.map((e) => (
        <tr key={e.id} className="hover:bg-page/60">
          <td className="px-4 py-2.5"><a className={`${link} font-mono text-[13px]`} href={`#/events/${e.id}`}>{e.type}</a></td>
          <td className="px-4 py-2.5 tabular-nums">{e.record_count || "—"}</td>
          <td className="px-4 py-2.5 tabular-nums">{bytes(e.body_bytes)}</td>
          <td className="px-4 py-2.5 text-muted">{when(e.created_at)}</td>
          <td className="px-4 py-2.5"><Id value={e.id} /></td>
        </tr>
      ))}
    </Table>
  );
}

export function Events({ conn }: P) {
  const { data, error } = useApi<List<EventItem>>(conn, "/v1/events?limit=100", 5000);
  return (
    <Page title="Events">
      {error && <Notice>{error}</Notice>}
      <EventTable events={data?.data ?? []} />
    </Page>
  );
}

export function EventDetail({ conn, id }: P & { id: string }) {
  const ev = useApi<EventItem>(conn, `/v1/events/${id}`);
  const deliveries = useApi<List<Delivery>>(conn, `/v1/events/${id}/deliveries`, 3000);
  return (
    <Page title={<span className="font-mono text-[22px]">{ev.data?.type ?? "Event"}</span>} aside={<Id value={id} short={false} />}>
      {ev.error && <Notice>{ev.error}</Notice>}
      <Section title="Deliveries">
        <DeliveryTable conn={conn} rows={deliveries.data?.data ?? []} onChange={deliveries.reload} />
      </Section>
      <Section title="Payload" aside={ev.data && <span className="text-[14px] text-muted">Kept until {when(ev.data.expires_at)}</span>}>
        {ev.data?.purged ? (
          <Notice>This payload passed its retention period and was deleted. Delivery history is kept; replay is no longer possible.</Notice>
        ) : (
          <pre className="max-h-[480px] overflow-auto rounded-md border border-line bg-surface p-4 font-mono text-[13px] leading-relaxed">
            {ev.data ? JSON.stringify(ev.data.payload, null, 2) : "Loading…"}
          </pre>
        )}
      </Section>
    </Page>
  );
}

// ---- Deliveries ------------------------------------------------------------------------------------

const filters = [
  { key: "", label: "All" },
  { key: "failed", label: "Failed" },
  { key: "pending", label: "Waiting to retry" },
  { key: "succeeded", label: "Delivered" },
];

export function Deliveries({ conn }: P) {
  const [status, setStatus] = useState("failed");
  const { data, error, reload } = useApi<List<Delivery>>(conn, `/v1/deliveries?limit=100${status ? `&status=${status}` : ""}`, 3000);
  return (
    <Page title="Deliveries">
      <div role="tablist" aria-label="Filter deliveries" className="flex gap-1 border-b border-line">
        {filters.map((f) => (
          <button key={f.key} role="tab" aria-selected={status === f.key} onClick={() => setStatus(f.key)}
            className={`-mb-px border-b-2 px-3 py-2 text-[14px] ${status === f.key ? "border-cobalt font-medium text-ink" : "border-transparent text-muted hover:text-ink"}`}>
            {f.label}
          </button>
        ))}
      </div>
      {status === "failed" && (
        <p className="max-w-[70ch] text-[14px] text-muted">
          These webhooks used up their retries or were rejected by your endpoint. Fix the endpoint, then send them again. They keep their original
          <code className="mx-1 font-mono text-ink">webhook-id</code> so your receiver can ignore duplicates.
        </p>
      )}
      {error && <Notice>{error}</Notice>}
      <DeliveryTable conn={conn} rows={data?.data ?? []} onChange={reload} />
    </Page>
  );
}

function DeliveryTable({ conn, rows, onChange }: P & { rows: Delivery[]; onChange: () => void }) {
  const [open, setOpen] = useState<string | null>(null);
  return (
    <Table head={["Status", "Attempts", "Last response", "Created", "Event", ""]} empty="No deliveries match.">
      {rows.map((d) => [
        <tr key={d.id} className="hover:bg-page/60">
          <td className="px-4 py-2.5"><Status value={d.status} />{d.replay_of && <span className="ml-2 text-[13px] text-muted">resend</span>}</td>
          <td className="px-4 py-2.5 tabular-nums">{d.attempt_count} of {d.max_attempts}</td>
          <td className="px-4 py-2.5">{d.last_status_code ?? <span className="text-muted">{d.last_error ?? "—"}</span>}</td>
          <td className="px-4 py-2.5 text-muted">{when(d.created_at)}</td>
          <td className="px-4 py-2.5"><a className={link} href={`#/events/${d.event_id}`}><span className="font-mono text-[13px]">{d.event_id.slice(0, 8)}…</span></a></td>
          <td className="px-4 py-2.5 text-right">
            <button className={`${link} text-[14px]`} aria-expanded={open === d.id} onClick={() => setOpen(open === d.id ? null : d.id)}>
              {open === d.id ? "Hide attempts" : "Show attempts"}
            </button>
          </td>
        </tr>,
        open === d.id && (
          <tr key={d.id + "-attempts"} className="bg-page/50">
            <td colSpan={6} className="px-4 py-4"><Attempts conn={conn} delivery={d} onChange={onChange} /></td>
          </tr>
        ),
      ])}
    </Table>
  );
}

function Attempts({ conn, delivery, onChange }: P & { delivery: Delivery; onChange: () => void }) {
  const { data } = useApi<List<Attempt>>(conn, `/v1/deliveries/${delivery.id}/attempts`, delivery.status === "succeeded" ? undefined : 2000);
  const [result, setResult] = useState<{ tone: "ok" | "bad"; text: string } | null>(null);
  const canResend = delivery.status === "failed" || delivery.status === "succeeded" || delivery.status === "cancelled";

  async function resend() {
    try {
      const r = await api<Delivery>(conn, `/v1/deliveries/${delivery.id}/replay`, { method: "POST" });
      setResult({ tone: "ok", text: r.status === "pending" ? "Queued to send again." : "A resend is already in progress." });
      onChange();
    } catch (e) {
      setResult({ tone: "bad", text: e instanceof ApiError ? e.message : "Could not resend." });
    }
  }

  return (
    <div className="space-y-3">
      <ol className="space-y-1.5 text-[14px]">
        {data?.data.length === 0 && <li className="text-muted">No attempts yet.</li>}
        {data?.data.map((a) => (
          <li key={a.attempt_number} className="flex flex-wrap gap-x-4">
            <span className="w-20 text-muted">Attempt {a.attempt_number}</span>
            <span className={a.outcome === "succeeded" ? "text-ok" : "text-bad"}>{a.status_code ?? "no response"}</span>
            <span className="tabular-nums text-muted">{a.duration_ms} ms</span>
            <span className="text-muted">{when(a.started_at)}</span>
            {a.error && <span className="text-muted">{a.error}</span>}
          </li>
        ))}
      </ol>
      {delivery.status === "pending" && delivery.attempt_count > 0 && (
        <p className="text-[14px] text-muted">Next attempt {when(delivery.next_attempt_at)}.</p>
      )}
      {canResend && <Button variant="quiet" onClick={resend}>Send again</Button>}
      {result && <Notice tone={result.tone}>{result.text}</Notice>}
    </div>
  );
}
