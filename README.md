# RelayStack

Managed infrastructure for reliably moving data between software systems. This repository is **Phase 0**: a
technical proof of concept. It shows a file uploaded over HTTPS becoming validated, signed webhook deliveries,
with durable state, retries, replay, tenant isolation and metering.

> **Not for public exposure.** Phase 0 has not had a security review. Run it locally only. See
> [docs/SECURITY_MODEL.md](docs/SECURITY_MODEL.md) for what is implemented and what is outstanding.

## What works

1. An operator CLI creates an organization, a project, its development and production environments, and a scoped API key.
2. `POST /v1/destinations` registers a webhook endpoint and returns a Standard Webhooks signing secret once.
3. `POST /v1/inboxes` creates an inbox with an explicit CSV schema.
4. `POST /v1/inboxes/{id}/files` streams a CSV to object storage and commits the file row and its processing job in one transaction.
5. The worker parses the file as a stream with hard limits, validates every row, and writes JSON record batches as events. Each event's deliveries are written in the same transaction (transactional outbox).
6. The worker delivers signed webhooks through an SSRF-safe HTTP client. It retries transient failures with bounded exponential backoff and dead-letters exhausted deliveries.
7. Failed or delivered webhooks can be replayed within the retention window, with the same `webhook-id`.
8. Usage is metered from the first byte. A polling experiment reuses the same delivery engine, and a small dashboard shows all of it.

## Quick start

Requirements: .NET 10 SDK, Docker, Node 22 (dashboard only), `curl` and `jq` (walkthrough only).

```bash
docker compose up --build -d          # Postgres, migrations, API :8080, worker, dev receiver :8090

# Create an organization, project and development API key. The key is the last line (rs_dev_...), shown once.
docker compose run --rm -T migrate bootstrap --org "Acme Dev" --project "Payroll sync"
export RELAYSTACK_API_KEY=rs_dev_...   # paste the key; the dashboard asks for the same key

./samples/curl/walkthrough.sh         # the whole workflow, step by step

docker compose --profile dashboard up -d dashboard   # http://localhost:5173
```

Running the hosts with `dotnet run` instead of containers is described in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Repository layout

```
src/RelayStack.Platform     Modular monolith: tenancy, security, inboxes/processing, deliveries, polling, metering, persistence
src/RelayStack.Api          REST API host + operator CLI (migrate, bootstrap, create-key)
src/RelayStack.Worker       Background host: jobs, deliveries, polling, retention
src/RelayStack.DevReceiver  Development-only webhook receiver and synthetic REST API
tests/                      xUnit unit tests and Testcontainers integration tests
web/dashboard               React + TypeScript + Tailwind dashboard
samples/                    Synthetic CSV data, schema, curl walkthrough, C# upload and receiver samples
deploy/                     Dockerfile and local Postgres init
docs/                       Product, architecture, security, delivery semantics, costs, ADRs, risks
```

## Verification status

Phase 0 was built where NuGet and Docker Hub could not be reached. To test anyway, EF Core 10.0.15, Npgsql 10.0.4
and the Npgsql EF provider were built from their GitHub source tags. Small local stand-ins replaced xUnit (a
reflection runner) and Testcontainers (a fresh database on a local Postgres 16). The repository references the normal
nuget.org packages; the stand-ins are not committed. Since then the real packages have been used in GitHub Actions and
in a local `docker compose` run.

| Area | Status |
|---|---|
| Platform library, API, worker, dev receiver, samples | Built and run against real EF Core/Npgsql and Postgres 16 |
| 102 unit tests, 29 integration tests | Pass in GitHub Actions with real xUnit v3 and Testcontainers (Postgres 17) |
| curl walkthrough and C# samples | Run end to end against API, worker and receiver started as separate processes |
| Dashboard | `npm run build` passes; checked in Chrome against the live stack, desktop and mobile widths |
| `docker compose` and Dockerfile | Images built and the walkthrough run end to end with `docker compose` on macOS |
| OpenTelemetry and OpenAPI wiring | Compiles against the real packages. **Trace export and the generated OpenAPI document not yet checked.** |
| GitHub Actions workflow | Green: build, unit and integration tests, dashboard build, Compose config; landing page deploys to Pages |
| AWS CDK stack | Design only ([docs/AWS_INTEGRATION_PLAN.md](docs/AWS_INTEGRATION_PLAN.md)); nothing provisioned |

First thing to do on a normal machine: `dotnet test RelayStack.slnx` and `docker compose up --build`.

## Research

The market and technical research that motivated this phase is kept privately and is not part of this
repository. Phase 0 follows its recommended wedge (Inbox first, Poll second, one delivery
engine) and records where it deliberately differs in [ADR-013](docs/DECISIONS.md#adr-013-reconciliation-with-the-research-report).

## Documentation

[Product vision](docs/PRODUCT_VISION.md) ·
[Architecture](docs/ARCHITECTURE.md) ·
[Domain model](docs/DOMAIN_MODEL.md) ·
[API contracts](docs/API_CONTRACTS.md) ·
[Security model](docs/SECURITY_MODEL.md) ·
[Delivery semantics](docs/DELIVERY_SEMANTICS.md) ·
[Infrastructure costs](docs/INFRASTRUCTURE_COSTS.md) ·
[AWS integration plan](docs/AWS_INTEGRATION_PLAN.md) ·
[Roadmap](docs/ROADMAP.md) ·
[Decisions (ADRs)](docs/DECISIONS.md) ·
[Risks](docs/RISKS.md) ·
[Development](docs/DEVELOPMENT.md)

## License

All rights reserved. The source is published to be read, not reused. See [LICENSE](LICENSE).
