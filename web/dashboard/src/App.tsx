import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError, loadConnection, saveConnection, type Connection, type Project } from "./api";
import { Deliveries, EnvBadge, EventDetail, Events, FileDetail, InboxDetail, Inboxes, Overview } from "./pages";
import { Button, Notice } from "./ui";

function useHashRoute() {
  const [hash, setHash] = useState(() => window.location.hash || "#/overview");
  useEffect(() => {
    const on = () => setHash(window.location.hash || "#/overview");
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  return hash.replace(/^#\//, "").split("/");
}

const nav = [
  { href: "#/overview", label: "Overview", match: "overview" },
  { href: "#/inboxes", label: "Inboxes", match: "inboxes" },
  { href: "#/events", label: "Events", match: "events" },
  { href: "#/deliveries", label: "Deliveries", match: "deliveries" },
];

export function App() {
  const [conn, setConn] = useState<Connection | null>(loadConnection);
  const [project, setProject] = useState<Project | null>(null);
  const route = useHashRoute();

  useEffect(() => {
    if (conn) api<Project>(conn, "/v1/project").then(setProject).catch(() => setProject(null));
  }, [conn]);

  if (!conn) return <Connect onConnected={(c) => (saveConnection(c), setConn(c))} />;

  const [section, id] = route;
  const active = section === "files" ? "inboxes" : section;
  let page;
  if (section === "inboxes" && id) page = <InboxDetail conn={conn} id={id} />;
  else if (section === "inboxes") page = <Inboxes conn={conn} />;
  else if (section === "files" && id) page = <FileDetail conn={conn} id={id} />;
  else if (section === "events" && id) page = <EventDetail conn={conn} id={id} />;
  else if (section === "events") page = <Events conn={conn} />;
  else if (section === "deliveries") page = <Deliveries conn={conn} />;
  else page = <Overview conn={conn} />;

  return (
    <div className="min-h-screen md:grid md:grid-cols-[220px_1fr]">
      <aside className="border-b border-line bg-surface md:min-h-screen md:border-b-0 md:border-r">
        <div className="flex items-center justify-between px-5 py-5 md:block">
          <a href="#/overview" className="flex items-center gap-2.5 text-[17px] font-semibold">
            <Mark /> RelayStack
          </a>
          {project && (
            <div className="text-[13px] md:mt-4">
              <div className="truncate text-muted">{project.organization.name}</div>
              <div className="mt-0.5 flex items-center gap-2">
                <span className="truncate">{project.project.name}</span>
                <EnvBadge kind={project.environment.kind} />
              </div>
            </div>
          )}
        </div>
        <nav aria-label="Main" className="flex gap-1 overflow-x-auto px-3 pb-3 md:flex-col md:pb-0">
          {nav.map((n) => (
            <a key={n.href} href={n.href} aria-current={active === n.match ? "page" : undefined}
              className={`whitespace-nowrap rounded-md px-3 py-2 text-[14px] ${active === n.match ? "bg-cobalt-soft font-medium text-cobalt" : "text-muted hover:bg-page hover:text-ink"}`}>
              {n.label}
            </a>
          ))}
        </nav>
        <div className="hidden px-5 py-6 md:block">
          <button className="text-[13px] text-muted hover:text-ink" onClick={() => (saveConnection(null), setConn(null), setProject(null))}>
            Disconnect
          </button>
        </div>
      </aside>
      <main>{page}</main>
    </div>
  );
}

/** Logo: a line passing through relay points, drawn rather than borrowed. */
function Mark() {
  return (
    <svg width="22" height="22" viewBox="0 0 22 22" aria-hidden>
      <path d="M2 15 L8 7 L14 15 L20 7" fill="none" stroke="var(--color-cobalt)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="8" cy="7" r="2" fill="var(--color-ink)" />
      <circle cx="14" cy="15" r="2" fill="var(--color-ink)" />
    </svg>
  );
}

function Connect({ onConnected }: { onConnected: (c: Connection) => void }) {
  const [baseUrl, setBaseUrl] = useState(import.meta.env.VITE_API_BASE_URL ?? "http://localhost:8080");
  const [apiKey, setApiKey] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    const c = { baseUrl: baseUrl.trim(), apiKey: apiKey.trim() };
    try {
      await api<Project>(c, "/v1/project");
      onConnected(c);
    } catch (err) {
      setError(err instanceof ApiError
        ? err.status === 401 ? "That API key was not accepted. Keys start with rs_dev_ or rs_prod_." : err.message
        : "The API could not be reached at that address. Is it running, and does it allow this page's origin (Cors:AllowedOrigins)?");
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="flex min-h-screen items-center justify-center px-6">
      <form onSubmit={submit} className="w-full max-w-[420px] space-y-5 rounded-md border border-line bg-surface p-7">
        <div className="flex items-center gap-2.5 text-[20px] font-semibold"><Mark /> RelayStack</div>
        <p className="text-[14px] text-muted">
          Connect with a project API key. The key stays in this browser tab and is cleared when you close it.
        </p>
        <label className="block text-[14px]">
          <span className="font-medium">API address</span>
          <input value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} required
            className="mt-1.5 w-full rounded-md border border-line px-3 py-2 font-mono text-[13px]" />
        </label>
        <label className="block text-[14px]">
          <span className="font-medium">API key</span>
          <input value={apiKey} onChange={(e) => setApiKey(e.target.value)} required type="password" autoComplete="off" placeholder="rs_dev_…"
            className="mt-1.5 w-full rounded-md border border-line px-3 py-2 font-mono text-[13px]" />
        </label>
        {error && <Notice>{error}</Notice>}
        <Button type="submit" disabled={busy}>{busy ? "Connecting…" : "Connect"}</Button>
      </form>
    </main>
  );
}
