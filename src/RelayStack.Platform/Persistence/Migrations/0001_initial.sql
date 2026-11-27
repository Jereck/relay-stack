-- RelayStack schema v1 (Phase 0).
--
-- Tenant isolation is enforced in three layers:
--   1. Every tenant-owned row carries organization_id, and cross-table references use composite
--      foreign keys that include organization_id, so a row can never point at another tenant's row.
--   2. Row-level security: the API connects as relaystack_app and only sees rows whose
--      organization_id equals the per-connection setting relaystack.organization_id.
--      If the setting is missing, app_current_org() is NULL and no rows match (fail closed).
--   3. Application-level EF Core query filters (see RelayStackDbContext).
--
-- The worker connects as relaystack_worker, which has an explicit permissive policy because it
-- processes work for all tenants. It does NOT have BYPASSRLS (not grantable on managed Postgres).

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'relaystack_app') THEN
        CREATE ROLE relaystack_app NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'relaystack_worker') THEN
        CREATE ROLE relaystack_worker NOLOGIN;
    END IF;
END
$$;

CREATE FUNCTION app_current_org() RETURNS uuid
    LANGUAGE sql STABLE
AS $$ SELECT NULLIF(current_setting('relaystack.organization_id', true), '')::uuid $$;

-- ---------------------------------------------------------------------------------------------
-- Tenancy
-- ---------------------------------------------------------------------------------------------

CREATE TABLE organizations (
    id          uuid PRIMARY KEY,
    name        text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE projects (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id),
    name            text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id)
);

CREATE TABLE environments (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    project_id      uuid NOT NULL,
    kind            text NOT NULL CHECK (kind IN ('development', 'production')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (project_id, kind),
    UNIQUE (organization_id, project_id, id),
    FOREIGN KEY (organization_id, project_id) REFERENCES projects (organization_id, id)
);

CREATE TABLE api_keys (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    project_id      uuid NOT NULL,
    environment_id  uuid NOT NULL,
    name            text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    prefix          text NOT NULL UNIQUE,
    secret_hash     bytea NOT NULL,
    scopes          text[] NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz,
    revoked_at      timestamptz,
    FOREIGN KEY (organization_id, project_id, environment_id) REFERENCES environments (organization_id, project_id, id)
);

-- ---------------------------------------------------------------------------------------------
-- Delivery destinations
-- ---------------------------------------------------------------------------------------------

CREATE TABLE webhook_destinations (
    id                        uuid PRIMARY KEY,
    organization_id           uuid NOT NULL,
    project_id                uuid NOT NULL,
    environment_id            uuid NOT NULL,
    name                      text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    url                       text NOT NULL CHECK (length(url) <= 2048),
    signing_secret_ciphertext bytea NOT NULL,
    enabled                   boolean NOT NULL DEFAULT true,
    timeout_seconds           int NOT NULL CHECK (timeout_seconds BETWEEN 1 AND 30),
    max_attempts              int NOT NULL CHECK (max_attempts BETWEEN 1 AND 20),
    created_at                timestamptz NOT NULL DEFAULT now(),
    updated_at                timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id),
    UNIQUE (organization_id, project_id, environment_id, id),
    FOREIGN KEY (organization_id, project_id, environment_id) REFERENCES environments (organization_id, project_id, id)
);

-- ---------------------------------------------------------------------------------------------
-- Inboxes and files
-- ---------------------------------------------------------------------------------------------

CREATE TABLE inboxes (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL,
    project_id        uuid NOT NULL,
    environment_id    uuid NOT NULL,
    name              text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    destination_id    uuid,
    validation_schema jsonb NOT NULL,
    max_file_bytes    bigint NOT NULL CHECK (max_file_bytes BETWEEN 1 AND 1073741824),
    retention_days    int NOT NULL CHECK (retention_days BETWEEN 1 AND 30),
    created_at        timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id),
    UNIQUE (organization_id, project_id, environment_id, id),
    FOREIGN KEY (organization_id, project_id, environment_id) REFERENCES environments (organization_id, project_id, id),
    FOREIGN KEY (organization_id, project_id, environment_id, destination_id)
        REFERENCES webhook_destinations (organization_id, project_id, environment_id, id)
);

CREATE TABLE files (
    id                    uuid PRIMARY KEY,
    organization_id       uuid NOT NULL,
    project_id            uuid NOT NULL,
    environment_id        uuid NOT NULL,
    inbox_id              uuid NOT NULL,
    original_name         text NOT NULL CHECK (length(original_name) BETWEEN 1 AND 255),
    content_type          text NOT NULL,
    size_bytes            bigint NOT NULL,
    sha256                bytea NOT NULL,
    storage_key           text NOT NULL,
    status                text NOT NULL CHECK (status IN ('received', 'processing', 'completed', 'failed', 'expired')),
    idempotency_key       text CHECK (length(idempotency_key) <= 255),
    total_rows            bigint,
    valid_rows            bigint,
    invalid_rows          bigint,
    error_code            text,
    error_message         text,
    received_at           timestamptz NOT NULL DEFAULT now(),
    processing_started_at timestamptz,
    completed_at          timestamptz,
    expires_at            timestamptz NOT NULL,
    UNIQUE (organization_id, id),
    UNIQUE (inbox_id, idempotency_key),
    FOREIGN KEY (organization_id, project_id, environment_id, inbox_id)
        REFERENCES inboxes (organization_id, project_id, environment_id, id)
);
CREATE INDEX ix_files_inbox_received ON files (inbox_id, received_at DESC);

CREATE TABLE validation_errors (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organization_id uuid NOT NULL,
    file_id         uuid NOT NULL,
    row_number      bigint NOT NULL,
    column_name     text,
    code            text NOT NULL,
    message         text NOT NULL,
    FOREIGN KEY (organization_id, file_id) REFERENCES files (organization_id, id)
);
CREATE INDEX ix_validation_errors_file ON validation_errors (file_id, row_number);

-- ---------------------------------------------------------------------------------------------
-- Durable jobs (Postgres-backed queue; see docs/DECISIONS.md ADR-004)
-- ---------------------------------------------------------------------------------------------

CREATE TABLE jobs (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id),
    kind            text NOT NULL CHECK (kind IN ('process_file', 'poll_source')),
    subject_id      uuid NOT NULL,
    status          text NOT NULL CHECK (status IN ('pending', 'running', 'succeeded', 'failed')),
    attempts        int NOT NULL DEFAULT 0,
    max_attempts    int NOT NULL CHECK (max_attempts BETWEEN 1 AND 20),
    run_after       timestamptz NOT NULL DEFAULT now(),
    locked_by       text,
    locked_until    timestamptz,
    last_error      text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    completed_at    timestamptz
);
-- At most one active job per subject: duplicate enqueues are rejected by the database.
CREATE UNIQUE INDEX ux_jobs_active_subject ON jobs (kind, subject_id) WHERE status IN ('pending', 'running');
CREATE INDEX ix_jobs_pending ON jobs (run_after) WHERE status = 'pending';
CREATE INDEX ix_jobs_running ON jobs (locked_until) WHERE status = 'running';

-- ---------------------------------------------------------------------------------------------
-- Events, deliveries (the transactional outbox) and attempts
-- ---------------------------------------------------------------------------------------------

CREATE TABLE events (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL,
    project_id        uuid NOT NULL,
    environment_id    uuid NOT NULL,
    source_kind       text NOT NULL CHECK (source_kind IN ('inbox_file', 'poll_source')),
    source_id         uuid NOT NULL,
    dedupe_key        text NOT NULL,
    type              text NOT NULL,
    body              text,          -- exact bytes that are signed and sent; NULL once purged
    body_bytes        int NOT NULL,
    record_count      int NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    expires_at        timestamptz NOT NULL,
    purged_at         timestamptz,
    UNIQUE (organization_id, id),
    -- Duplicate processing protection: re-running a job cannot create a second event for the same unit of work.
    UNIQUE (source_id, dedupe_key),
    FOREIGN KEY (organization_id, project_id, environment_id) REFERENCES environments (organization_id, project_id, id)
);
CREATE INDEX ix_events_source ON events (source_id, created_at);
CREATE INDEX ix_events_env_created ON events (environment_id, created_at DESC);

CREATE TABLE deliveries (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL,
    event_id         uuid NOT NULL,
    destination_id   uuid NOT NULL,
    status           text NOT NULL CHECK (status IN ('pending', 'in_flight', 'succeeded', 'failed', 'cancelled')),
    attempt_count    int NOT NULL DEFAULT 0,
    max_attempts     int NOT NULL CHECK (max_attempts BETWEEN 1 AND 20),
    next_attempt_at  timestamptz NOT NULL DEFAULT now(),
    locked_by        text,
    locked_until     timestamptz,
    last_attempt_at  timestamptz,
    last_status_code int,
    last_error       text,
    replay_of        uuid REFERENCES deliveries (id),
    created_at       timestamptz NOT NULL DEFAULT now(),
    completed_at     timestamptz,
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, event_id) REFERENCES events (organization_id, id),
    FOREIGN KEY (organization_id, destination_id) REFERENCES webhook_destinations (organization_id, id)
);
-- One original delivery per (event, destination); replays are additional rows linked by replay_of.
CREATE UNIQUE INDEX ux_deliveries_original ON deliveries (event_id, destination_id) WHERE replay_of IS NULL;
-- At most one active replay per delivery: makes replay requests idempotent while one is pending or in flight.
CREATE UNIQUE INDEX ux_deliveries_active_replay ON deliveries (replay_of) WHERE replay_of IS NOT NULL AND status IN ('pending', 'in_flight');
CREATE INDEX ix_deliveries_due ON deliveries (next_attempt_at) WHERE status = 'pending';
CREATE INDEX ix_deliveries_inflight ON deliveries (organization_id, locked_until) WHERE status = 'in_flight';
CREATE INDEX ix_deliveries_event ON deliveries (event_id);

CREATE TABLE delivery_attempts (
    id              uuid PRIMARY KEY,
    organization_id uuid NOT NULL,
    delivery_id     uuid NOT NULL,
    attempt_number  int NOT NULL,
    started_at      timestamptz NOT NULL,
    duration_ms     int NOT NULL,
    outcome         text NOT NULL CHECK (outcome IN ('succeeded', 'retryable_failure', 'permanent_failure')),
    status_code     int,
    error           text,
    request_bytes   int NOT NULL,
    response_bytes  int NOT NULL,
    UNIQUE (delivery_id, attempt_number),
    FOREIGN KEY (organization_id, delivery_id) REFERENCES deliveries (organization_id, id)
);

-- ---------------------------------------------------------------------------------------------
-- Metering and audit
-- ---------------------------------------------------------------------------------------------

CREATE TABLE usage_records (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organization_id uuid NOT NULL,
    project_id      uuid NOT NULL,
    environment_id  uuid NOT NULL,
    metric          text NOT NULL,
    quantity        bigint NOT NULL CHECK (quantity >= 0),
    subject_id      uuid,
    occurred_at     timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (organization_id, project_id, environment_id) REFERENCES environments (organization_id, project_id, id)
);
CREATE INDEX ix_usage_env_metric_time ON usage_records (environment_id, metric, occurred_at);

CREATE TABLE audit_log (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organization_id uuid NOT NULL REFERENCES organizations (id),
    actor_type      text NOT NULL,
    actor_id        text NOT NULL,
    action          text NOT NULL,
    target_type     text NOT NULL,
    target_id       text NOT NULL,
    remote_ip       text,
    details         jsonb,
    occurred_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_audit_org_time ON audit_log (organization_id, occurred_at DESC);

-- ---------------------------------------------------------------------------------------------
-- API key lookup. Authentication happens before the tenant is known, so the API role cannot read
-- api_keys directly. This SECURITY DEFINER function returns exactly one key by its public prefix.
-- ---------------------------------------------------------------------------------------------

CREATE FUNCTION auth_lookup_api_key(p_prefix text)
    RETURNS TABLE (id uuid, organization_id uuid, project_id uuid, environment_id uuid, secret_hash bytea,
                   scopes text[], expires_at timestamptz, revoked_at timestamptz, environment_kind text)
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path = public, pg_temp
AS $$
    SELECT k.id, k.organization_id, k.project_id, k.environment_id, k.secret_hash, k.scopes,
           k.expires_at, k.revoked_at, e.kind
    FROM api_keys k
    JOIN environments e ON e.id = k.environment_id
    WHERE k.prefix = p_prefix
$$;
REVOKE ALL ON FUNCTION auth_lookup_api_key(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION auth_lookup_api_key(text) TO relaystack_app;

-- ---------------------------------------------------------------------------------------------
-- Row-level security
-- ---------------------------------------------------------------------------------------------

ALTER TABLE organizations ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON organizations TO relaystack_app
    USING (id = app_current_org()) WITH CHECK (id = app_current_org());
CREATE POLICY worker_access ON organizations TO relaystack_worker USING (true) WITH CHECK (true);

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['projects', 'environments', 'api_keys', 'webhook_destinations', 'inboxes', 'files',
                             'validation_errors', 'jobs', 'events', 'deliveries', 'delivery_attempts',
                             'usage_records', 'audit_log']
    LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY tenant_isolation ON %I TO relaystack_app
                        USING (organization_id = app_current_org())
                        WITH CHECK (organization_id = app_current_org())', t);
        EXECUTE format('CREATE POLICY worker_access ON %I TO relaystack_worker USING (true) WITH CHECK (true)', t);
    END LOOP;
END
$$;

-- ---------------------------------------------------------------------------------------------
-- Grants (least privilege; no DELETE for the API except where needed)
-- ---------------------------------------------------------------------------------------------

GRANT USAGE ON SCHEMA public TO relaystack_app, relaystack_worker;
GRANT EXECUTE ON FUNCTION app_current_org() TO relaystack_app, relaystack_worker;

GRANT SELECT ON organizations, projects, environments TO relaystack_app;
GRANT SELECT, INSERT, UPDATE ON webhook_destinations, inboxes, files, jobs, deliveries TO relaystack_app;
GRANT SELECT ON validation_errors, events, delivery_attempts, usage_records TO relaystack_app;
GRANT INSERT ON usage_records, audit_log TO relaystack_app;
GRANT SELECT ON audit_log TO relaystack_app;

GRANT SELECT ON organizations, projects, environments, webhook_destinations, inboxes TO relaystack_worker;
GRANT SELECT, INSERT, UPDATE ON files, jobs, events, deliveries, delivery_attempts TO relaystack_worker;
GRANT SELECT, INSERT, DELETE ON validation_errors TO relaystack_worker;
GRANT SELECT, INSERT ON usage_records, audit_log TO relaystack_worker;

GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO relaystack_app, relaystack_worker;
