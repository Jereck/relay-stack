# API contracts (v1)

Base URL locally: `http://localhost:8080`. JSON uses `snake_case`. Errors are RFC 9457 problem details with a stable
`code`. Lists are newest first, `?limit=` (1-100, default 25) and `?starting_after=<id>` for the next page
(`next_cursor` in the response). All endpoints need `Authorization: Bearer rs_dev_…` unless noted.

The machine-readable spec is generated at runtime in Development: `GET /openapi/v1.json`. Save it to
`docs/openapi.json` once verified on a normal machine (see README, verification status).

## Endpoints

| Method and path | Scope | Purpose |
|---|---|---|
| `GET /healthz` | none | Liveness |
| `GET /v1/project` | any | Organization, project, environment and scopes of the key |
| `POST /v1/destinations` | `destinations:write` | Create a webhook destination; returns `signing_secret` once |
| `GET /v1/destinations`, `GET /v1/destinations/{id}` | `destinations:read` | List / get (never returns the secret) |
| `POST /v1/inboxes` | `inboxes:write` | Create an inbox with a validation schema |
| `GET /v1/inboxes`, `GET /v1/inboxes/{id}` | `inboxes:read` | List / get |
| `POST /v1/inboxes/{id}/files?filename=` | `files:write` | Upload a CSV as the raw body (`Content-Type: text/csv`). `Idempotency-Key` supported |
| `GET /v1/inboxes/{id}/files` | `files:read` | Files in an inbox |
| `GET /v1/files/{id}` | `files:read` | Processing status and row counts |
| `GET /v1/files/{id}/validation-errors?after_row=&limit=` | `files:read` | Rejected rows, ordered by row |
| `GET /v1/events?source_id=&type=` | `events:read` | Events (no payloads) |
| `GET /v1/events/{id}` | `events:read` | Event with its exact payload until purged |
| `GET /v1/events/{id}/deliveries` | `deliveries:read` | Deliveries of an event |
| `GET /v1/deliveries?status=&event_id=` | `deliveries:read` | Deliveries; `status=failed` is the dead-letter view |
| `GET /v1/deliveries/{id}`, `GET /v1/deliveries/{id}/attempts` | `deliveries:read` | Delivery and attempt history |
| `POST /v1/deliveries/{id}/replay` | `deliveries:replay` | Resend a terminal delivery |
| `GET /v1/usage?from=&to=` | `usage:read` | Metered totals and amplification ratios |
| `POST /v1/poll-sources`, `GET /v1/poll-sources[/{id}]` | `polling:write` / `polling:read` | Polling experiment |

## Status codes worth knowing

| Code | `code` | When |
|---|---|---|
| 202 | | Upload accepted; processing is asynchronous |
| 200 | | Upload repeated with the same `Idempotency-Key` and identical content, or replay already active |
| 400 | `validation_failed` | Bad request body; `errors` lists every problem |
| 401 | `unauthorized` | Missing, malformed, unknown, revoked or expired key, or a key used against the wrong environment marker |
| 403 | `insufficient_scope` | Key lacks the scope |
| 404 | `not_found` | Not found **or belongs to another tenant/environment** (never 403, to avoid confirming existence) |
| 409 | `idempotency_key_reused`, `delivery_in_progress` | Same key with different content; replay of a non-terminal delivery |
| 410 | `replay_window_expired` | Event payload purged |
| 413 | `file_too_large` | Over the inbox or platform limit |
| 415 | `unsupported_media_type` | Upload not `text/csv` |
| 429 | `rate_limited` | With `Retry-After` |

## Validation schema

```json
{
  "unknown_columns": "ignore",
  "columns": [
    { "name": "employee_id", "type": "string", "required": true, "max_length": 16 },
    { "name": "department", "type": "string", "allowed_values": ["ENG", "OPS"] },
    { "name": "hours_worked", "type": "decimal", "required": true }
  ]
}
```

Types: `string`, `integer`, `decimal` (`.` separator), `boolean` (`true/false/yes/no/1/0`), `date` (`yyyy-MM-dd`),
`datetime` (ISO 8601 with offset, normalised to UTC), `email`. No regular expressions or expressions (ADR-007).
File-level failures: `missing_columns`, `duplicate_columns`, `unknown_columns`, `empty_file`, `invalid_encoding`,
`unterminated_quote`, `unexpected_quote`, `text_after_quote`, `field_too_long`, `too_many_fields`,
`record_too_long`, `too_many_records`. Row-level codes: `field_count`, `required`, `max_length`, `not_allowed`,
`invalid_{type}`.

## Webhooks

Headers (Standard Webhooks): `webhook-id` (the event id, stable across retries and replays), `webhook-timestamp`
(Unix seconds), `webhook-signature` (`v1,<base64 HMAC-SHA256 of "{id}.{timestamp}.{body}">`). Receivers must verify
the signature, reject timestamps more than 5 minutes off, de-duplicate on `webhook-id`, and respond 2xx quickly.

```json
{
  "id": "01a219f7-ffb8-71fb-a2f4-13d6f048b64a",
  "type": "inbox.file.records",
  "created_at": "2026-11-26T22:26:52.000Z",
  "environment": "development",
  "data": {
    "inbox_id": "…", "file_id": "…", "file_name": "timesheets.csv",
    "batch_index": 0, "first_row": 1, "last_row": 40, "record_count": 35,
    "records": [ { "employee_id": "E1000", "hours_worked": 40, "period_start": "2026-11-02", "overtime": false } ]
  }
}
```

Event types: `inbox.file.records` (one per batch of ≤100 valid rows / 256 KiB), `inbox.file.processed`,
`inbox.file.failed` (summary with counts and error), `poll.record.created`, `poll.record.updated`.

Batches can arrive out of order and more than once. Use `file_id` + `batch_index` to reassemble, and the
`inbox.file.processed` event's `batch_count` to know when a file is complete.

## Examples

* curl: [`samples/curl/walkthrough.sh`](../samples/curl/walkthrough.sh) covers every step of the workflow.
* C#: [`samples/csharp/Program.cs`](../samples/csharp/Program.cs) uploads with an idempotency key, waits for the
  result, and runs a receiver that verifies signatures and de-duplicates.

```bash
curl -H "Authorization: Bearer $RELAYSTACK_API_KEY" -H "Content-Type: text/csv" \
     -H "Idempotency-Key: payroll-2026-11-15" \
     --data-binary @samples/data/timesheets_valid.csv \
     "http://localhost:8080/v1/inboxes/$INBOX_ID/files?filename=timesheets.csv"
```
