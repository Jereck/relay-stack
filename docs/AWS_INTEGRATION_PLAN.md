# AWS integration plan

**Nothing has been provisioned. Do not deploy anything without explicit authorization.** This plan maps the local
implementation to AWS and describes a minimal development stack for when deployment is approved.

## Mapping

| Local component | AWS service | Notes |
|---|---|---|
| HTTPS upload endpoint (API) | CloudFront → ALB or API Gateway → ECS Fargate | Large uploads go directly to S3 via presigned PUT/multipart once the API issues upload sessions; the API then commits the file row after verifying size and checksum. |
| (future) SFTP | AWS Transfer Family with S3 backend, or self-run shared SFTP nodes | Transfer Family is about $216/month per protocol endpoint; shared nodes are cheaper for small tenants. |
| `FileSystemObjectStore` | S3 (`IObjectStore` S3 implementation) | Same key layout `org/{id}/…`; SSE-KMS; Block Public Access; lifecycle rules as a backstop for retention. |
| Postgres jobs and outbox | RDS for PostgreSQL (same schema, same `SKIP LOCKED` queue) | SQS is added only where it pays: fan-out to many workers, bursty polling, or decoupling S3 notifications. |
| Worker loops | ECS Fargate service (long-running) | Lambda suits short polling jobs later; file processing and delivery loops are long-lived and connection-pooled, which favours Fargate. |
| Event notifications | EventBridge | For Transfer Family "upload completed" events and future customer targets (EventBridge, SQS as destinations). |
| `SecretProtector` keys | KMS (envelope encryption, per-tenant data keys) | Keys never in configuration. |
| Connection strings, pepper | Secrets Manager | Rotated; injected as ECS secrets. |
| JSON logs, OTel | CloudWatch Logs and metrics (or OTLP collector) | Alarms: DLQ growth, blocked-destination spikes, job failures, auth failures. |
| Dashboard | S3 + CloudFront | Strict CSP; API on a separate origin with CORS allowlist. |
| Outbound webhooks and polls | Private-subnet workers behind a NAT gateway with an Elastic IP | Gives customers a static IP to allowlist (a differentiator in the research report). The SSRF client still validates every address. |

## Completed vs partial SFTP uploads

**Problem.** An SFTP client can disconnect mid-transfer. With an S3 backend, the bytes received so far may still
become an S3 object. If processing were triggered by S3 `ObjectCreated` events, an interrupted file would be
processed as if it were complete, and half a payroll file would be delivered as a "successful" upload.

**What AWS provides.** Transfer Family managed workflows distinguish full uploads (`OnUpload`) from partial uploads
(`OnPartialUpload`). A partial upload is defined as a file that was still open when the session disconnected
([WorkflowDetails](https://docs.aws.amazon.com/transfer/latest/APIReference/API_WorkflowDetails.html),
[managed workflows](https://docs.aws.amazon.com/transfer/latest/userguide/transfer-workflows.html)).

The research report's v1 sketch detects files through S3 `ObjectCreated` events. That is exactly the failure mode
above, so this plan replaces it.

**Design rule: a file becomes a RelayStack upload only through an explicit "complete" signal.**

1. SFTP users write to a landing prefix that RelayStack never processes directly. No S3 event notification on it
   triggers processing.
2. The `OnUpload` workflow (or the Transfer Family upload-completed event on EventBridge) calls a small RelayStack
   endpoint. That endpoint copies or tags the object into `org/{id}/…/original`, computes SHA-256, and inserts the
   `files` row and job in one transaction, exactly like the HTTPS path.
3. The `OnPartialUpload` workflow moves the object to a quarantine prefix, records a `file.partial` audit entry and
   emits an `inbox.file.partial` event so the customer knows a transfer broke. Quarantine expires by lifecycle rule.
4. Defence in depth, configurable per inbox: accept only files renamed from `*.part`/`*.tmp` to their final name,
   and optionally require a manifest or checksum file (`name.csv.sha256`) whose hash must match before processing.
5. The same rule applies to self-run SFTP nodes: they stream to S3 multipart and call `CompleteMultipartUpload` only
   on a clean close; an aborted session aborts the multipart upload, so no object ever appears.
6. Tests: interrupted transfers (kill the client mid-upload) must produce zero `files` rows and one quarantine
   object. This test does not exist yet because SFTP is out of scope for Phase 0.

## Minimal CDK development stack (C#, design)

One stack, one environment, removable with one command. Costs are in INFRASTRUCTURE_COSTS.md (about $38-40/month if
left running, about $0.05/hour).

```
RelayStackDevStack
├── Vpc: 2 AZs, public subnets only, no NAT gateway
├── SecurityGroups: api (8080 from DeveloperCidr only), worker (egress only), db (5432 from api/worker)
├── Rds: PostgreSQL 17, db.t4g.micro, single-AZ, 20 GB gp3, isolated subnets,
│        RemovalPolicy.DESTROY, DeletionProtection=false, BackupRetention=1 day
├── Secrets: owner/app/worker credentials (Secrets Manager), pepper, data-key wrap
├── Kms: one customer-managed key (PendingWindow 7 days)
├── Bucket: SSE-KMS, BlockPublicAccess, EnforceSSL, AutoDeleteObjects=true, RemovalPolicy.DESTROY
├── Ecs Cluster (Fargate, ARM64)
│   ├── migrate task (run once): RelayStack.Api migrate
│   ├── api service: 0.25 vCPU / 0.5 GB, desired 1, public IP
│   └── worker service: 0.25 vCPU / 0.5 GB, desired 1, public IP
└── LogGroups: 3-day retention, RemovalPolicy.DESTROY
```

Sketch of the entry point (not compiled in Phase 0):

```csharp
var app = new App();
new RelayStackDevStack(app, "RelayStackDev", new StackProps
{
    Env = new Amazon.CDK.Environment { Account = "<account>", Region = "us-east-1" },
    Tags = new Dictionary<string, string> { ["project"] = "relaystack", ["env"] = "dev", ["ttl"] = "destroy-after-testing" },
});
app.Synth();
```

Before deploying (only after authorization):

1. `cdk synth` and review the template; confirm no NAT gateway, ALB or Multi-AZ resources are present.
2. Set an AWS Budget alarm at $20 and $50 for the account.
3. `cdk deploy RelayStackDev --parameters DeveloperCidr=<your-ip>/32`.

## Teardown

```bash
cdk destroy RelayStackDev
```

Then verify that nothing billable remains:

* RDS: no instance and no **manual snapshots** (`aws rds describe-db-snapshots --snapshot-type manual`). Automated
  backups are removed with the instance when `DeleteAutomatedBackups` is true.
* S3: the bucket is gone (AutoDeleteObjects empties it first).
* KMS: the key is "pending deletion" (charged until the 7-day window ends).
* CloudWatch: log groups removed; no leftover alarms.
* ECR: delete repository images if images were pushed.
* Elastic IPs and ENIs: none left (`aws ec2 describe-addresses`).
* Cost Explorer the next day shows no ongoing charges tagged `project=relaystack`.
