# Delivery semantics

**RelayStack delivers at least once. It never claims exactly-once execution or delivery.** Receivers must be
idempotent, keyed on `webhook-id`.

## Guarantees

| Statement | Mechanism | Tested by |
|---|---|---|
| An upload that receives `202` is durable: its bytes are stored and a processing job exists | Object written (temp file → rename) before the DB transaction that inserts `files` + `jobs`; on DB failure the object is deleted | `VerticalSliceTests`, upload code path |
| Accepted work is not lost if a worker crashes | Jobs and deliveries are leased (`locked_until`); after expiry any worker reclaims them | `Jobs_held_by_a_crashed_worker…`, `Deliveries_held_by_a_crashed_worker…`, `Running_worker_host…` |
| Re-running a job does not create duplicate events or deliveries | Deterministic batch numbering + `UNIQUE(source_id, dedupe_key)`; deliveries inserted only with a newly inserted event; partial unique index on original deliveries | `Reprocessing_a_file_does_not_duplicate…` |
| An event and its deliveries become visible together | Same transaction (transactional outbox) | Code structure (`EventWriter` requires a transaction) |
| Event ids are stable | `webhook-id` = event id across retries and replays | `Transient_failures…`, `Exhausted_deliveries…` |
| Transient failures are retried with bounded exponential backoff | 408, 425, 429, 5xx, timeouts, network errors → retry; `Retry-After` honoured up to the cap | `Transient_failures…`, unit tests |
| Attempts are bounded | `max_attempts` per destination (1-20, default 8); then `failed` (dead letter) | `Exhausted_deliveries…` |
| Permanent responses are not retried | Other 3xx/4xx, blocked destinations → `failed` immediately | `Client_errors_fail_permanently…` |
| Replay is possible only while the payload is retained | `410` after purge | `Replay_is_refused_after_the_retention_window` |
| Replay requests are idempotent while active | Unique index on `replay_of` for pending/in-flight replays | `Exhausted_deliveries…` |
| Uploads are idempotent per inbox | `Idempotency-Key` + `UNIQUE(inbox_id, idempotency_key)`; same content → `200` original, different → `409` | `Upload_idempotency_key…` |

## Where duplicates come from (expected, by design)

1. A worker sends a webhook, the receiver accepts it, and the worker crashes before committing the outcome. After the
   lease expires the delivery is sent again with the same `webhook-id`. The lost attempt still counts towards
   `attempt_count`.
2. The receiver answers after our timeout (10 s default, max 30 s). We record a retryable failure and resend.
3. A lease expires while a slow attempt is still running (lease 60 s > maximum timeout 30 s, so only under extreme
   stalls). Both attempts are recorded; only the lease holder updates the delivery.
4. Manual replay deliberately resends.

## Ordering

No ordering guarantee between events, including batches of one file. Batches carry `batch_index`, `first_row` and
`last_row`; `inbox.file.processed` carries `batch_count`.

## Timing

| Setting | Default | Notes |
|---|---|---|
| Delivery timeout | 10 s per attempt (1-30 per destination) | Connect timeout 5 s |
| Backoff | delay ceiling = 5 s × 2^(n-2), capped at 1 h; actual delay = ceiling/2 + random × ceiling/2 | Attempt 2 waits 2.5-5 s; attempt 8 waits 160-320 s |
| Delivery lease | 60 s | Must exceed max timeout |
| Job timeout / lease | 300 s / 600 s | Job retried up to 3 times with linear backoff, then the file is `failed` (`processing_failed`) |
| Payload retention | 7 days default (1-30 per inbox) | Files deleted and event bodies purged by the retention sweeper |

## Concurrency

* Each organization has at most `MaxConcurrentPerOrganization` (default 4) deliveries in flight. The claim query
  counts live leases. Under several concurrent dispatchers this is a **soft** limit: two claims can race. A hard
  limit needs a per-tenant semaphore row or advisory lock (outstanding).
* There is no per-destination concurrency limit yet.

## What is not guaranteed

* Exactly-once anything.
* Delivery to destinations that resolve to private, loopback, link-local or metadata addresses (permanently failed).
* Retention of payloads beyond `retention_days`.
* Detection of deleted records in polling (only new and changed records are emitted).
* Durability of the local filesystem object store beyond the machine it runs on.
