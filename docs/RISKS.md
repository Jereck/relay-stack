# Risks

Ordered by severity × likelihood. "Mitigation" lists what exists today; "Next" what is still needed.

| # | Risk | Mitigation today | Next |
|---|---|---|---|
| 1 | **Worker cross-tenant bug.** A worker bug could mix one tenant's data into another's. | Reduced (ADR-014): the worker role is under RLS and each item runs in a context scoped to its organization; composite FKs, tenant-bound object keys and AES-GCM associated data remain as further layers. Residual risk sits in the four SECURITY DEFINER claim functions. | Independent review of the claim functions; property-based tests over multi-tenant worker runs. |
| 2 | **Parts verified only on stand-ins.** OpenTelemetry export and the generated OpenAPI document have not been checked with the real packages. | Tests now pass in CI with real xUnit and Testcontainers; Compose images build and run. | Point the OTLP exporter at a local collector once; generate and commit `openapi.json`. |
| 3 | **Retry amplification drives cost.** A dead endpoint multiplies egress and worker time by `max_attempts`; JSON is about 2.3× the CSV size. | Bounded attempts, permanent 4xx, backoff to 1 h, per-org concurrency, retry bytes metered. | Auto-disable destinations after consecutive failures; compact batch encoding; price on delivered records. |
| 4 | **Postgres as queue hits limits.** Claim queries and outbox growth contend with API traffic. | Partial indexes on due rows; small claim batches; leases. | Retention for delivered rows and attempts; partitioning by month; move hot fan-out to SQS (ADR-004). |
| 5 | **Soft per-tenant concurrency.** Concurrent dispatchers can exceed the cap briefly. | Claim counts live leases. | Semaphore rows or advisory locks per organization. |
| 6 | **Sensitive customer data in retained artefacts.** Payroll-like CSVs, event bodies and validation messages hold personal data. | Retention purge of files and bodies; secrets encrypted; no payloads in lists or logs. | Purge validation messages with the file; encryption of event bodies at rest; data-processing terms; deletion API. |
| 7 | **SSRF policy gaps.** New address ranges, IPv6 tricks, or future features (custom headers, proxies) re-open SSRF. | Connect-time validation of every resolved address, exhaustive range tests, no proxies or redirects. | Egress through a dedicated NAT/egress proxy with an allowlist-by-default network policy; periodic review. |
| 8 | **Hostile files.** Very wide rows or pathological quoting exhaust CPU or memory. | Hard limits on fields, record length, record count; strict UTF-8; job timeout. | Fuzz the CSV reader; per-tenant processing quotas. |
| 9 | **Operational load on a part-time founder.** Pager duty for delivery outages. | Batch, retry-tolerant workloads; durable state; replay. | Alerting with sensible thresholds, runbooks, status page before paying customers. |
| 10 | **Market risk.** Webhook delivery and iPaaS are crowded; the differentiation (files and polling into reliable events) may not be enough. | Phase 0 deliberately small and cheap. | Validate with 5-10 design partners before Phase 1. |
| 11 | **Dependency drift.** EF Core/Npgsql pinned to versions built locally; .NET 11 exists. | Central package versions; .NET 10 LTS. | Dependabot; upgrade cadence aligned with LTS releases. |
| 12 | **Dev defaults leak into production.** | Startup refuses development secrets and the receiver allowlist outside Development. | Secrets only from Secrets Manager in cloud environments. |
