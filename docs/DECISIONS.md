# Architecture decision records

Short ADRs. Status: Accepted unless noted. Revisit when the stated trigger occurs.

## ADR-001 Modular monolith, two hosts
**Context.** One part-time founder; Phase 0 needs speed and few moving parts. **Decision.** One `RelayStack.Platform`
library organised by module folders, run by an API host and a worker host. No microservices, no internal message bus
between modules. **Consequences.** One deployable library and one schema; modules communicate through the database
and plain method calls. **Revisit** if one module needs independent scaling that a second worker deployment cannot give.

## ADR-002 PostgreSQL as system of record, accessed with EF Core
**Decision.** PostgreSQL for all state; EF Core for CRUD and queries; hand-written SQL where precision matters (queue
claims, `ON CONFLICT`). **Consequences.** Transactions span upload, outbox and usage writes.

## ADR-003 Tenant isolation: composite FKs + RLS + EF filters
**Decision.** Every tenant row has `organization_id`. Composite FKs include it, RLS enforces it for the API role
(failing closed), and EF filters add environment scoping. Separate DB roles for API, worker and owner. The worker
uses an explicit permissive policy rather than `BYPASSRLS`, because `BYPASSRLS` cannot be granted on RDS.
**Consequences.** Defence in depth; the worker path remains the weakest layer (see RISKS.md).

## ADR-004 Postgres-backed job queue and transactional outbox, no broker in Phase 0
**Context.** Uploads must not be lost; events must not be published without their state change. **Decision.** Jobs
and deliveries are rows claimed with `FOR UPDATE SKIP LOCKED` and leases. Events and deliveries are written in the
same transaction (the deliveries table is the outbox). **Consequences.** No dual-write problem, no extra
infrastructure, and crash recovery comes from lease expiry. Throughput is bounded by Postgres; expected to be ample
for the first hundreds of tenants. **Revisit** with SQS when claim queries dominate DB load or fan-out is needed.

## ADR-005 Local equivalents: filesystem object store; no LocalStack, no MinIO
**Context.** LocalStack has needed an account and auth token since March 2026; MinIO community distribution is
archived. **Decision.** `IObjectStore` with a filesystem implementation (atomic temp-then-rename) locally; S3 in
production; SeaweedFS (Apache 2.0) as the S3-compatible test target when the S3 adapter is written.
**Consequences.** S3-specific behaviour is untested in Phase 0.

## ADR-006 Hand-written SQL migrations
**Decision.** Embedded, ordered `.sql` files applied by a tiny migrator under an advisory lock. **Why.** RLS policies,
roles, grants, partial indexes and SECURITY DEFINER functions are first-class here; EF migrations would wrap them in
raw SQL anyway, and the EF design-time tooling adds a dependency. **Consequences.** The EF model must be kept in step
with the SQL by hand; integration tests catch drift.

## ADR-007 Declarative validation only, own CSV reader
**Decision.** A typed column schema (types, required, max length, allowed values). No regex, no expressions, no
customer code. CSV parsed by a small streaming RFC 4180 reader with hard limits instead of a general library.
**Why.** Linear-time validation, full control over limits and error codes, no ReDoS, and no executable customer logic.
**Revisit** when a transformation language is designed (Transform service).

## ADR-008 At-least-once delivery with Standard Webhooks signatures
**Decision.** Stable event ids as `webhook-id`; HMAC-SHA256 signatures per the Standard Webhooks spec; retry 408, 425,
429, 5xx and network errors with bounded exponential backoff and equal jitter; other responses permanent; dead letter
= `failed` deliveries; replay creates new deliveries. Per-org in-flight cap is soft. **Consequences.** Receivers must
de-duplicate; documented in DELIVERY_SEMANTICS.md.

## ADR-009 Operator-provisioned tenants in Phase 0
**Decision.** No sign-up, no user accounts: an operator CLI creates organizations and keys. The dashboard uses an API
key held in `sessionStorage`. **Revisit** before any external user touches the system.

## ADR-010 SSRF defence at connect time with an exact, development-only exception
**Decision.** All customer URLs go through one `HttpClient` whose connect callback resolves DNS and validates every
address before connecting to it. The development receiver is reachable only via an exact `host:port` allowlist that
is honoured for development environments on a Development host, and startup fails if it is set elsewhere.
**Consequences.** No production code path can bypass the policy by configuration alone.

## ADR-011 Metering as append-only usage rows
**Decision.** Every cost-driving action writes a usage row in the same transaction as the action. Totals and ratios are
computed on read. **Revisit** with rollups when usage tables grow large.

## ADR-012 One polling experiment through the existing engine
**Decision.** Poll sources produce events via the same `EventWriter` and delivery dispatcher, with per-transition
dedupe keys. **Result.** It needed no changes to delivery code, only a new event source kind, which confirms the
engine is input-agnostic.

## ADR-013 Reconciliation with the research report
**Context.** The private market, product and technical research report (Nov 26, 2026; kept outside this repository) proposes
the Inbox wedge, Poll as the second input on the same engine, and a v1 architecture. Phase 0 follows its direction
but not every assumption.

| Research proposal | Phase 0 choice | Why / when to revisit |
|---|---|---|
| JSON Schema validation per inbox | Typed column schema (ADR-007) | Linear-time, no regex, clearer row errors for CSV. Revisit when JSON Lines/XML inputs arrive; a JSON Schema subset can compile to the same validator. |
| Events `file.received`, `file.rejected`, `records.batch`, `file.completed` | `inbox.file.records`, `inbox.file.processed`, `inbox.file.failed`, `poll.record.*` | Namespaced by source so Poll fits. No `file.received` event yet. **Decide before the API is public**, as event names are a contract. |
| Retries with backoff over 24 h, up to 12 attempts | 8 attempts, delay capped at 1 h (about 10 min total by default) | Short for real outages. Add a max-age policy (for example 24 h) and per-endpoint circuit breakers before customers rely on it. |
| HTTPS upload via pre-signed S3 URLs | Streamed through the API | Simpler locally; presigned uploads in Phase 1 (AWS_INTEGRATION_PLAN.md). |
| Detect via S3 `ObjectCreated` → SQS | Explicit "complete" signal only | `ObjectCreated` also fires for objects created by interrupted SFTP sessions; see the partial-upload rule. |
| Transactional outbox → SQS delivery queue | Outbox rows claimed directly from Postgres | ADR-004; SQS added when load or fan-out requires it. |
| Static egress IP on every paid plan | Not in Phase 0 | Production egress through a NAT gateway with an Elastic IP (AWS_INTEGRATION_PLAN.md). |
| Destination ports 443/8443 only; block 169.254.170.2 | Adopted | Stricter than the original Phase 0 rule. |
| `test`/`live` environments, `rs_live_` keys | `development`/`production`, `rs_dev_`/`rs_prod_` | Follows the Phase 0 brief. |
| Prefixed ids (`inb_`, `evt_`) | UUIDv7 | Cosmetic; decide with the event names. |
| `Idempotency-Key` on resource creation | Only on uploads (and replay is idempotent by design) | Add to `POST /v1/inboxes` and `/v1/destinations` before SDKs. |
| Backpressure: stop pulling a tenant's work while its endpoint fails | Per-org in-flight cap only | Add circuit breakers and pause after prolonged failure. |
| Quartz.NET or EventBridge Scheduler for Poll | `next_poll_at` claim in Postgres | Enough for the experiment. |

## ADR-014 Worker under row-level security; cross-tenant discovery only through claim functions
**Context.** The worker role had permissive `USING (true)` policies, so isolation in the worker rested on every
query filtering by organization by hand. A single missed predicate could read or write another tenant's rows.
BYPASSRLS is not an option (not grantable on RDS, and it would remove the safety net entirely).
**Decision.** Migration 0003 gives `relaystack_worker` the same `tenant_isolation` policy as the API role. Work is
found through four SECURITY DEFINER functions owned by the migration role (`worker_claim_jobs`,
`worker_claim_deliveries`, `worker_claim_poll_sources`, `worker_orgs_with_expired_payloads`). Each leases a clamped
number of due rows and returns only those (the last returns organization ids only), and only the worker role may
execute them. Every item is then handled in a DbContext scoped to its organization (`WorkerDbFactory.For`): EF
filters and the `relaystack.organization_id` session setting both apply, and an unscoped worker context sees nothing.
The CLI and tests use a separate `Operator` mode (owner role) so "see everything" is never a worker capability.
**Consequences.** A worker bug can now only affect the organization of the item in hand. The claim functions become
security-critical code and must be part of any RLS review. Per-item contexts cost a `set_config` round trip per
connection open, which is negligible next to the HTTP delivery itself.
