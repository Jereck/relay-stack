# Roadmap

## Phase 0 milestones (this repository)

Each milestone was implemented in order and tested before the next one began. "Verified" means it ran here against
real EF Core, Npgsql and Postgres. These checks used local stand-ins for xUnit and Testcontainers (see README).

| Milestone | Scope | Status |
|---|---|---|
| M0 Foundation | Monorepo, solution, central packages, docs, ADRs | Done |
| M1 Tenancy and security core | SQL schema, roles, RLS, composite FKs, API keys, bootstrap CLI, cross-tenant tests | Done, verified |
| M2 Ingestion | Destinations, inboxes, streaming upload with limits and idempotency, tenant-scoped storage, upload + job in one transaction | Done, verified |
| M3 Processing | Leased jobs, streaming CSV reader, schema validation, batches, outbox writes, idempotent re-runs | Done, verified |
| M4 Delivery | Standard Webhooks signing, SSRF-safe client, backoff, dead letter, replay, per-tenant concurrency, crash recovery | Done, verified |
| M5 Developer environment | Compose, dev receiver, metering, usage API, curl and C# samples, end-to-end run with real processes | Done; Compose images not built here |
| M6 Dashboard | Overview, inboxes, upload, results, validation errors, events, delivery attempts, replay | Done, checked in a browser |
| M7 Polling experiment | One synthetic REST source through the existing delivery engine | Done, verified |
| M8 AWS plan | Service mapping, partial SFTP upload design, CDK dev stack design, costs, teardown | Done (documents only) |
| M9 Worker tenant scoping | Worker role under RLS, SECURITY DEFINER claim functions, org-scoped worker contexts, `Operator` mode for CLI/tests (ADR-014) | Done, verified (29 integration tests; mutation-checked; live end-to-end run) |
| CI | GitHub Actions for build, tests, dashboard, compose config | Written, not run |

### Phase 0 definition of done

| Criterion | Status |
|---|---|
| The project runs locally | Yes, with `dotnet run` (verified); `docker compose` written, not built here |
| Provision an inbox through an API | Yes |
| Upload and process a CSV | Yes |
| Invalid records reported clearly | Yes (row, column, code, message; file-level error codes) |
| Valid records delivered as signed JSON webhooks | Yes |
| Delivery failures recorded and retried | Yes |
| Failed events can be replayed | Yes |
| Worker restarts do not silently lose accepted work | Yes (lease recovery tests for jobs and deliveries) |
| Cross-tenant access tests pass | Yes, with the stand-in runner; re-run with real xUnit pending |
| Resource usage recorded | Yes |
| Basic polling input reuses the delivery engine | Yes |
| Minimal dashboard | Yes |
| Remaining gaps documented | Yes: SECURITY_MODEL.md, RISKS.md, DOMAIN_MODEL.md |

## Immediate next steps

1. Run `dotnet test` with real xUnit v3 and Testcontainers, build the Compose images, and commit the generated
   `openapi.json`. Fix whatever the stand-ins hid.
2. Push to GitHub and get CI green.
3. S3 `IObjectStore` implementation tested against SeaweedFS and one real bucket.
4. Hard per-tenant concurrency (semaphore rows) and per-tenant quotas from usage records.
5. Security review of RLS (including the `worker_claim_*` functions), the SSRF client and authentication.

## Phase 1 candidates (commercial MVP)

* Self-service sign-up, dashboard login, key management UI.
* Inbox over SFTP (Transfer Family or shared nodes) with the partial-upload rules in AWS_INTEGRATION_PLAN.md.
* Presigned S3 uploads for large files.
* Destination secret rotation; auto-disable failing destinations; gzip webhook bodies.
* Poll: authenticated REST sources (API key, OAuth client credentials), pagination, cursors, rate limits.
* Usage-based billing with hard quotas (Stripe later, after metering has real data).
* Status page, alerting, runbooks.

## Explicitly out of scope until further notice

Production SFTP hosting, Stripe billing, enterprise SSO, customer code execution, workflow orchestration, SOAP, a
transformation language, marketplace integrations, Terraform providers, production SDK packages, multi-region,
microservices, Kubernetes.
