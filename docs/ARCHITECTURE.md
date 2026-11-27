# Architecture

## Shape

A **modular monolith**: one library (`RelayStack.Platform`) with explicit modules, and two thin hosts that run it in
different modes. Postgres is the only stateful dependency besides object storage.

```
             HTTPS (API key)                                          signed webhooks
  client ──────────────────────► RelayStack.Api ─┐            ┌──► customer endpoint
                                  (Tenant mode)  │            │      (SSRF-safe client)
                                                 ▼            │
                                         ┌──────────────┐     │
                         object store ◄──┤  PostgreSQL  ├─────┤
                         (files)         │ files, jobs, │     │
                                         │ events,      │     │
                                         │ deliveries,  │     │
                                         │ usage, audit │     │
                                         └──────▲───────┘     │
                                                │             │
                                  RelayStack.Worker ──────────┘
                                  (Worker mode): jobs · deliveries · polling · retention
```

## Modules (folders in `src/RelayStack.Platform`)

| Module | Responsibility |
|---|---|
| `Tenancy` | Organizations, projects, environments, the request `TenantContext`, operator provisioning |
| `Security` | API key format and hashing, AES-GCM secret protection, Standard Webhooks signing, redaction, SSRF policy and safe HTTP client |
| `Processing` | Streaming CSV reader, validation schema, record validator, `FileProcessor` |
| `Deliveries` | `EventWriter` (outbox), `DeliveryDispatcher`, retry policy, replay |
| `Jobs` | Postgres job queue with leases |
| `Polling` | Phase 0 polling experiment |
| `Metering` | Usage metric names and records |
| `Retention` | Payload expiry and purge |
| `Storage` | Object store interface, tenant-scoped keys, filesystem implementation |
| `Persistence` | `RelayStackDbContext`, RLS connection interceptor, SQL migrator and migrations |

## Data flow for one file

1. **Upload (API).** Authenticate the key, which sets the tenant. Resolve the inbox within the key's environment and
   stream the body to `org/{org}/project/{project}/inbox/{inbox}/{file}/original` with a size cap and SHA-256. Then,
   in one transaction, insert the `files` row, a `process_file` job and usage records. Respond `202`.
2. **Process (worker).** Claim the job (`FOR UPDATE SKIP LOCKED`, lease). Stream the object through strict UTF-8
   decoding, the CSV reader and the validator. Every 100 valid rows (or 256 KiB), insert one `events` row and one
   `deliveries` row in a transaction. Batch numbers are deterministic, so a re-run is deduplicated by
   `UNIQUE(source_id, dedupe_key)`. Finish with a `inbox.file.processed` or `inbox.file.failed` event and the file's
   final status.
3. **Deliver (worker).** Claim due deliveries, at most N in flight per organization. Sign the stored body, POST it
   through the SSRF-safe client, record the attempt, and move the delivery to `succeeded`, back to `pending` with
   backoff, or to `failed` (dead letter).
4. **Replay (API).** Create a new delivery row for the same event while the payload is retained.

## Hosts and access modes

| Host | DB role | EF access mode | Sees |
|---|---|---|---|
| API | `relaystack_app` | `Tenant` | Only the authenticated organization (RLS) and environment (EF filter) |
| Worker | `relaystack_worker` | `Worker` | Finds work through SECURITY DEFINER claim functions; then only the claimed item's organization (RLS + EF filter) |
| Operator CLI | `relaystack_owner` | `Operator` | Everything; runs migrations and provisioning |

The worker's loop is always: claim with `WorkerDbFactory.ForClaims()` (a context that sees no tenant rows and can
only call `worker_claim_*`), then handle each item with `WorkerDbFactory.For(organizationId)`. See ADR-014.

## Local and production equivalents

| Concern | Local (Phase 0) | Production direction |
|---|---|---|
| Database | Postgres 17 in Compose | RDS for PostgreSQL |
| Object storage | Filesystem (`FileSystemObjectStore`), temp-then-rename writes | S3 behind the same `IObjectStore` |
| Queue | Postgres jobs and outbox | Same; SQS added only for fan-out or bursty load (ADR-004) |
| Secrets | Dev values in config, guarded at startup | Secrets Manager + KMS |
| Webhook receiver | `RelayStack.DevReceiver`, exact host:port allowlist | Customer endpoints, public addresses only |
| Telemetry | JSON logs to stdout; OTLP if `OTEL_EXPORTER_OTLP_ENDPOINT` is set | CloudWatch or an OTLP backend |

### Why no LocalStack or MinIO

* Since March 23, 2026, LocalStack ships as a single image that needs an account and auth token, and the Community
  repository is archived. Depending on it would put a vendor account into every developer's and CI's path.
* MinIO stopped publishing community images and binaries in October 2025, entered maintenance mode in December 2025
  and was archived in 2026. It is also AGPLv3.
* Phase 0 needs neither: Postgres is the queue (ADR-004), and object storage sits behind `IObjectStore` with a
  filesystem implementation. When the S3 adapter is built, the local S3-compatible candidate is **SeaweedFS (Apache
  2.0)**; Garage is AGPLv3 and RustFS is younger. Behavioural differences from real S3 (consistency, multipart limits,
  IAM) mean S3-specific behaviour must also be tested against a real bucket before production.

## Observability

* Structured JSON logs to stdout, with timestamps in UTC. Secrets never reach logs: URLs are logged via
  `Redaction.Url`, errors via `Redaction.Text`.
* `ActivitySource`s `RelayStack.Processing` and `RelayStack.Delivery` wrap file processing and each delivery attempt.
  ASP.NET Core, HttpClient and Npgsql emit their own traces and metrics.
* OpenTelemetry SDK and OTLP export are wired in `src/Shared/Observability.cs`. **Unverified in Phase 0** (see README).
* Business-level state (attempts, outcomes, usage) lives in Postgres and is queryable through the API, which is more
  useful to customers than infrastructure metrics.
