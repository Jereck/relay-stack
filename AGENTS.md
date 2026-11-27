# AGENTS.md

Instructions for AI coding agents and new contributors working in this repository.

## Context

RelayStack Phase 0 is a proof of concept for managed integration infrastructure: CSV upload → validation → signed
webhooks, on a shared, at-least-once delivery engine. Read `README.md`, then `docs/ARCHITECTURE.md`,
`docs/SECURITY_MODEL.md` and `docs/DELIVERY_SEMANTICS.md` before changing behaviour.

## Commands

```bash
dotnet build RelayStack.slnx
dotnet test tests/RelayStack.UnitTests
dotnet test tests/RelayStack.IntegrationTests      # requires Docker
cd web/dashboard && npm ci && npm run build
docker compose config -q
```

## Rules

1. **Never weaken tenant isolation.** New tenant tables need `organization_id`, composite FKs, the
   `tenant_isolation` RLS policy for `relaystack_app` **and** `relaystack_worker`, least-privilege grants, an EF query
   filter, and a cross-tenant test. Worker code finds work only through `worker_claim_*` functions
   (`WorkerDbFactory.ForClaims()`) and handles each item with `WorkerDbFactory.For(organizationId)`. Never give the
   worker a permissive policy or BYPASSRLS (ADR-014).
2. **Never claim exactly-once.** Delivery is at-least-once; keep `webhook-id` stable and document new duplicate paths.
3. **All customer-controlled URLs go through `SafeHttpClientFactory`.** Do not add proxies, redirects or new
   exceptions. The development receiver allowlist must stay Development-only.
4. **No customer executable code** (no expressions, regex from users, scripting).
5. **Secrets:** never log or return them after creation; use `Redaction` for external text; encrypt with
   `SecretProtector` and bind to the organization.
6. **State changes and their events/deliveries commit in one transaction** (`EventWriter` enforces a transaction).
7. **Schema:** add a new numbered SQL migration; never edit an applied one. Keep the EF model in step.
8. **Meter** every new cost-driving action with a usage record.
9. **Do not provision cloud resources.** No `cdk deploy`, no AWS CLI mutations, without explicit human authorization.
10. **Do not mark work complete unless it was run.** State what was verified and how.
11. Prefer explicit code over abstractions; no new frameworks or packages without a reason recorded in `docs/DECISIONS.md`.
12. Use synthetic data only. Never commit real customer or employer data.

## Layout

`src/RelayStack.Platform` (modules by folder), `src/RelayStack.Api`, `src/RelayStack.Worker`,
`src/RelayStack.DevReceiver`, `tests/*`, `web/dashboard`, `samples/*`, `deploy/*`, `docs/*`.
