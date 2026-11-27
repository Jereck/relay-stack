# Product vision

## The company

RelayStack is developer-first managed infrastructure for moving data reliably between software systems. Customers
integrate through an API and SDKs and never operate the moving parts themselves: endpoints, workers, retries,
storage, signatures and audit trails.

The bet: most B2B integrations are boring, repetitive data movement (files from partners, records from APIs that do
not push) that every team rebuilds badly. A small number of primitives, run well and priced transparently, can
replace that undifferentiated work.

## Services (long term)

| Service | Purpose | Status |
|---|---|---|
| Inbox | Managed file ingestion (HTTPS now, SFTP later) with validation | Phase 0 slice |
| Poll | Scheduled REST/SOAP polling that turns "pull" APIs into events | Phase 0 experiment only |
| Gateway | Managed HTTP event ingestion | Not started |
| Transform | Declarative transformations (no customer code) | Not started |
| Transfer | Managed outbound file transfer | Not started |
| Flow | Composable pipelines | Not started |
| Connect | SDKs and developer tooling | Not started |

All inputs share one **reliable event delivery engine**. Phase 0 exists to prove that engine and its data model.

## First commercial candidates

**Inbox** and **Poll**. The research report (`docs/research/`) recommends Inbox as the wedge: "our customer or
partner can only send us files over SFTP, and we have to turn them into clean records in our app." Its differentiators
are records rather than files, everything on the cheapest paid plan, per-customer inboxes by API and legacy-friendly
parsing. Poll is the second input type on the same engine, and the report proposes demand tests for both before
production code. This proof of concept starts with HTTPS upload because the processing and delivery behind any
transport are the same; SFTP comes with the commercial platform.

The report also sets the conditions under which to stop or switch: fewer than 30 qualified sign-ups and no
pre-payments after six weeks of the validation test, prospects demanding SOC 2 before paying, or unwillingness to
carry a pager. Phase 0 is technical validation only and does not answer the demand question.

## Proof of concept vs commercial platform

| | Phase 0 proof of concept | Commercial platform |
|---|---|---|
| Users | One developer on a laptop | Self-service sign-up, organizations, teams |
| Transport | HTTPS upload | HTTPS, SFTP/FTPS (AWS Transfer Family or own nodes), presigned S3 |
| Inputs | CSV with explicit schema; one synthetic polling source | CSV, JSON lines, fixed-width, XML; generic REST/SOAP polling |
| Outputs | Signed webhooks | Webhooks, S3/SQS/EventBridge targets, customer buckets |
| Queue | Postgres (outbox + leased jobs) | Postgres outbox with SQS fan-out where it pays off |
| Storage | Local filesystem | S3 with per-tenant prefixes, KMS, optional BYO bucket |
| Auth | Operator-provisioned API keys | Keys + dashboard login (later SSO) |
| Billing | Metering only | Usage-based plans with hard quotas |
| Operations | `docker compose` | IaC, monitoring, alerting, runbooks |

## Principles

API-first, developer-first, secure by design, multi-tenant from line one, observable, minimal operational overhead,
modular monolith over microservices, explicit code over speculative frameworks. Never claim exactly-once.
