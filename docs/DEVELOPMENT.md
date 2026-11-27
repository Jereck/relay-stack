# Development

## Prerequisites

.NET 10 SDK (10.0.100 or later), Docker (Compose v2), Node 22 for the dashboard, `curl` and `jq` for the
walkthrough.

## Option A: everything in containers

```bash
docker compose up --build -d
docker compose run --rm -T migrate bootstrap --org "Acme Dev" --project "Payroll sync"   # last line is the key, shown once
export RELAYSTACK_API_KEY=rs_dev_...                                                         # paste it here
./samples/curl/walkthrough.sh
docker compose --profile dashboard up -d dashboard      # http://localhost:5173
docker compose down        # add -v to delete the database and stored files
```

Inside Compose the worker reaches the receiver as `http://dev-receiver:8080/webhooks` (the walkthrough default).

## Option B: hosts with `dotnet run` (fastest inner loop)

```bash
docker compose up -d postgres                                  # creates roles and the relaystack database
export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development

dotnet run --project src/RelayStack.Api --no-launch-profile -- bootstrap --org "Acme Dev" --project "Payroll sync"
export RELAYSTACK_API_KEY=rs_dev_...   # the last line printed above; shown once

dotnet run --project src/RelayStack.DevReceiver   # :8090   (terminal 1)
dotnet run --project src/RelayStack.Api           # :8080   (terminal 2)
dotnet run --project src/RelayStack.Worker        #         (terminal 3)

WEBHOOK_URL=http://localhost:8090/webhooks ./samples/curl/walkthrough.sh
cd web/dashboard && npm install && npm run dev    # :5173
```

Notes:

* `dotnet run --project` uses the **project directory** as the working directory. Pass absolute paths to the C#
  sample, for example `"$PWD/samples/data/timesheets_valid.csv"`.
* With no storage path configured, Development hosts share `$TMPDIR/relaystack/objects`. Outside Development,
  `RelayStack:Storage:RootPath` must be an absolute path, or startup fails.
* The dev receiver keeps webhook secrets in memory. After restarting it, register the destination secret again
  (`POST :8090/control/secrets`), or deliveries fail with 401.
* Other operator commands: `migrate`, and `create-key --org <id> --project <id> --environment production --scopes inboxes:read,files:write`.

## Tests

```bash
dotnet test tests/RelayStack.UnitTests            # pure logic, no Docker
dotnet test tests/RelayStack.IntegrationTests     # needs Docker (Testcontainers postgres:17-alpine)
```

Integration tests start the real API on a random loopback port, the dev receiver and a worker host, and drive the
worker step by step. Each test gets its own database cloned from a migrated template, because the worker claims work
across all tenants.

## Dev receiver controls

```bash
curl -X POST localhost:8090/control/mode -H 'Content-Type: application/json' -d '{"failNext":3}'          # 3 × 503, then 204
curl -X POST localhost:8090/control/mode -H 'Content-Type: application/json' -d '{"permanentStatus":422}' # always 422
curl -X POST localhost:8090/control/mode -H 'Content-Type: application/json' -d '{"delayMs":15000}'       # force timeouts
curl -X POST localhost:8090/control/reset
curl localhost:8090/received | jq
curl -X POST localhost:8090/synthetic/employees/mutate      # change the polling experiment's data
```

## Conventions

* Tenant-owned data: add `organization_id`, composite FKs, RLS policy (API and worker roles), grants and an EF filter in the same change.
* Worker code: claim with `WorkerDbFactory.ForClaims()`, then process each item with `WorkerDbFactory.For(organizationId)`.
  New cross-tenant discovery needs a new `worker_claim_*` SECURITY DEFINER function and a test that the API role cannot call it.
* Schema changes: new numbered file in `src/RelayStack.Platform/Persistence/Migrations`; never edit an applied one.
* Customer URLs: only through `SafeHttpClientFactory`.
* Anything logged or stored from external input goes through `Redaction`.
* Every new cost-driving action writes a usage record.
* JSON is snake_case; query parameters too (bind with `[FromQuery(Name = "...")]`).
