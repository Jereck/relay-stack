# Security model

Security is a Phase 0 requirement, but Phase 0 is **not reviewed and must not be exposed publicly**. This document
separates what is implemented (and tested) from what production still needs.

## Threats we design against

Cross-tenant data access; SSRF through customer-supplied URLs (including DNS rebinding and cloud metadata); leaked or
brute-forced API keys; secret disclosure in logs and responses; resource exhaustion via huge or hostile files;
replayed or forged webhooks; customer-supplied executable logic.

## Controls

| Control | Status | Detail |
|---|---|---|
| Tenant isolation in PostgreSQL | **Implemented** | Composite tenant FKs; RLS on all tenant tables for the API **and worker** roles, failing closed (worker scoping: ADR-014); separate DB roles per process; no API grant on `api_keys`. See DOMAIN_MODEL.md. |
| Environment isolation | **Implemented** | EF filters on organization + environment; keys are bound to one environment; `rs_dev_`/`rs_prod_` marker must match the DB. |
| Cross-tenant tests | **Implemented** | `TenantIsolationTests`: every read and write endpoint returns 404 for another tenant's ids; raw SQL under RLS; dev vs prod; scopes; revocation. |
| Tenant-scoped object storage | **Implemented (local)** | Keys built from ids only under `org/{id}/`; re-validated on read; path escape checks. **Production:** S3 prefix per org, IAM policy limited to the bucket, worker/API roles scoped, optional per-tenant KMS key or BYO bucket. |
| Hashed API keys | **Implemented** | 256-bit random secret; HMAC-SHA256 with a server pepper; constant-time compare; prefix lookup through a SECURITY DEFINER function; unknown prefixes still hash. **Outstanding:** key rotation endpoint, last-used tracking, self-service revocation. |
| Credential encryption | **Implemented (local keys)** | AES-256-GCM, versioned key ids, associated data binds ciphertext to organization and purpose, rotation-friendly decryption. **Production:** KMS envelope encryption with per-tenant data keys; keys never in config. |
| Webhook signatures | **Implemented** | Standard Webhooks (HMAC-SHA256, id + timestamp + body), verified against the spec's reference vector; secret shown once. **Outstanding:** secret rotation with dual signatures (verification already accepts several). |
| Secret redaction | **Implemented** | `Redaction.Url` (drops userinfo, query, fragment) and `Redaction.Text` (masks keys, `whsec_`, bearer tokens) on logged and stored errors; secrets never returned after creation. |
| SSRF protection | **Implemented** | URL validation at creation (https, ports 443/8443 only, no credentials, no literal private IPs); authoritative check at connect time on every resolved address. |
| DNS rebinding protection | **Implemented** | DNS is resolved inside `SocketsHttpHandler.ConnectCallback` and the socket connects to the exact address checked. Any private answer in a mixed answer blocks the request. |
| Private, loopback, link-local, metadata blocking | **Implemented** | IPv4/IPv6 ranges incl. 169.254.169.254 (instance metadata), 169.254.170.2 (ECS task credentials), `fd00:ec2::254`, CGNAT, ULA, IPv4-mapped, NAT64, 6to4; exhaustive unit tests. |
| Safe outbound HTTP | **Implemented** | No proxy, no redirects, no cookies, no decompression, 16 KiB header cap, 4 KiB response read cap, per-attempt timeout, pooled connection lifetime 2 min. |
| Development receiver exception | **Implemented** | Exact `host:port` allowlist, honoured only when the host environment is Development **and** the destination's environment is `development`. Startup fails if configured elsewhere. Metadata and link-local are never allowed. Covered by unit and integration tests. |
| Rate limiting | **Implemented** | Token bucket per API key (20 rps, burst 100), uploads 60/min per key, anonymous 30 rps per IP; `429` with `Retry-After`. Per-instance only. **Production:** distributed limits (edge/WAF + per-tenant counters). |
| Resource quotas | **Partial** | Per-org delivery concurrency (soft); per-inbox file size; retention cap. **Outstanding:** per-tenant storage, monthly volume and inbox count quotas enforced from usage records. |
| Upload restrictions | **Implemented** | `text/csv` only; per-inbox and platform size caps enforced while streaming (and by `Content-Length` up front); empty files rejected; file names sanitised; content never executed. **Outstanding:** malware scanning if non-CSV content types are added. |
| CSV parser limits | **Implemented** | Field ≤16 KiB, ≤200 fields, record ≤64 KiB, ≤1M records, strict UTF-8, strict quoting, bounded memory; failures are permanent and reported. |
| Payload retention | **Implemented** | Per-inbox retention (1-30 days); sweeper deletes files and purges event bodies; replay refused after purge. **Outstanding:** retention on validation-error messages, which can echo customer data. |
| Audit logging | **Partial** | Org creation, key creation, destination/inbox/poll-source creation, uploads, replays; API role can only insert/read. **Outstanding:** audit for reads of payloads, export endpoint, tamper evidence. |
| No customer executable code | **Implemented** | Declarative schema only; no expressions or regex. |
| Transport security | **Outstanding** | Local is plain HTTP. Production: TLS at CloudFront/ALB, HSTS, TLS to RDS (`sslmode=verify-full`). |
| Dashboard auth | **Phase 0 only** | API key pasted into the browser, kept in `sessionStorage`. Production needs real user auth, CSRF-safe sessions and a strict CSP. |
| OpenAPI document | Development only | Not served outside Development. |
| Dependency and container scanning | **Outstanding** | Add Dependabot/`dotnet list package --vulnerable`, image scanning, SBOM. |

## Before any public exposure (minimum)

1. Independent review of RLS policies, the SSRF client and the authentication handler.
2. TLS everywhere, real secrets in Secrets Manager/KMS, development defaults impossible to load (already enforced at startup).
3. Distributed rate limiting and per-tenant quotas.
4. ~~Worker-side tenant scoping~~ Implemented (ADR-014). Include the four `worker_claim_*` SECURITY DEFINER functions in the RLS review.
5. Penetration test of upload, webhook and polling paths; fuzzing of the CSV reader.
6. Incident response basics: alerting on auth failures, blocked-destination spikes and DLQ growth.
