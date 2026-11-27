# Infrastructure costs and metering

Phase 0 runs locally at zero cloud cost. This document records what is metered, what amplification we measured, and
what the smallest cloud footprint would cost. All AWS prices are US East (N. Virginia), on-demand, and must be
re-checked before any deployment.

## What is metered (from day one)

| Metric | Recorded when | Cost driver |
|---|---|---|
| `files_uploaded`, `bytes_ingested` | Upload committed | Request handling, S3 PUT, ingress |
| `storage_bytes_written`, `storage_byte_days_committed` | Upload committed (size × retention days) | S3 storage for the promised retention |
| `records_processed`, `records_invalid`, `processing_ms` | File finished | Worker CPU |
| `events_created`, `event_bytes_created` | Event inserted | Postgres storage and I/O |
| `delivery_attempts`, `delivery_bytes_sent` | Every attempt, success or not | Worker time, egress |
| `delivery_bytes_delivered` | Successful attempts | Value actually delivered |
| `poll_requests`, `poll_bytes_fetched` | Each poll | Worker time, ingress |

`GET /v1/usage` returns totals and ratios. Records are append-only rows with organization, project, environment and
subject id, so billing and **hard quotas** can later be computed per tenant from real consumption.

## Measured amplification

From the local end-to-end walkthrough (synthetic timesheets, 41 rows, 3,661 bytes CSV):

| Measurement | Value |
|---|---|
| CSV in | 3,661 B |
| Events out (35 records + completion summary) | 7,955 B + 405 B = 8,360 B |
| **Input → output amplification** | **≈2.3×** (JSON field names repeated per record, typed values, envelope) |
| Same file with a receiver failing every attempt (5 attempts per delivery) | 5× the event bytes sent, 0 delivered |
| Session total across successful and failing runs | 3.66 bytes sent per byte received; 3.3 attempts per event |

Implications:

* Egress scales with **JSON size × attempts**, not CSV size. A customer whose endpoint is down multiplies our
  egress and worker time by `max_attempts` (default 8) without receiving anything.
* Pricing must be on **records or delivered events**, with retries either capped or metered separately.
  `delivery_bytes_sent - delivery_bytes_delivered` is the retry overhead and is already recorded.
* Controls already in place: bounded attempts, permanent failure on 4xx, backoff up to 1 h, per-org concurrency.
  Candidates: auto-disable destinations after N consecutive failures, gzip-encoded webhook bodies, compact batch
  encoding (column array + row arrays) to cut the 2.3× towards about 1.2×.

## Smallest cloud development stack (design, not deployed)

Designed to avoid the usual fixed costs: no NAT gateway (tasks in public subnets with public IPs, inbound locked to
the developer's IP) and no load balancer.

| Resource | Size | Monthly if left running 24/7 |
|---|---|---|
| ECS Fargate, API task | 0.25 vCPU, 0.5 GB, ARM ($0.03238/vCPU-h, $0.00356/GB-h) | ≈ $7.20 |
| ECS Fargate, worker task | same | ≈ $7.20 |
| Public IPv4 for the two tasks | $0.005/h each | ≈ $7.30 |
| RDS PostgreSQL db.t4g.micro, single-AZ | ≈ $11.68/month instance | ≈ $11.70 |
| RDS gp3 storage, 20 GB | *assumption: ≈ $0.115/GB-month, verify* | ≈ $2.30 |
| S3, SQS, KMS (1 key), CloudWatch Logs | low dev volume; *KMS ≈ $1/key-month, Logs ≈ $0.50/GB ingested, verify* | ≈ $2-4 |
| **Total** | | **≈ $38-40/month**, about **$0.05/hour** |

Run it only while testing and destroy it afterwards (see AWS_INTEGRATION_PLAN.md). Free-tier credits can cover RDS
for new accounts.

What production adds, roughly: an ALB (about $16/month base) or CloudFront in front of the API, a NAT gateway or VPC
endpoints for private tasks (NAT about $33/month per AZ plus $0.045/GB), Multi-AZ RDS (about 2×), and **AWS Transfer
Family if managed SFTP is offered** (about $216/month per protocol endpoint at $0.30/hour, which is why the earlier
research suggested shared self-run SFTP nodes for small customers).

## Sources

* Fargate rates: [AWS Fargate pricing](https://aws.amazon.com/fargate/pricing/), via
  [Vantage](https://www.vantage.sh/blog/fargate-pricing) and [usage.ai, Nov 2026](https://www.usage.ai/?p=12788).
* RDS db.t4g.micro: [Aiven RDS calculator](https://aiven.io/tools/instances/db.t4g.micro),
  [Bytebase](https://www.bytebase.com/dbcost/rds/instance/db.t4g.micro/).
* ALB and NAT hourly rates: [dev.to, verified May 2026](https://dev.to/dspv/what-does-aws-fargate-actually-cost-per-environment-5538).
* Transfer Family, public IPv4, S3, CloudWatch figures: the earlier RelayStack infrastructure research report.
