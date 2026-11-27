-- Phase 0 polling experiment (Part 9). Deliberately minimal: one synthetic REST source type.
-- events.source_kind and jobs.kind already allow 'poll_source' (0001).

CREATE TABLE poll_sources (
    id                uuid PRIMARY KEY,
    organization_id   uuid NOT NULL,
    project_id        uuid NOT NULL,
    environment_id    uuid NOT NULL,
    name              text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    url               text NOT NULL CHECK (length(url) <= 2048),
    record_id_field   text NOT NULL CHECK (length(record_id_field) BETWEEN 1 AND 100),
    interval_seconds  int NOT NULL CHECK (interval_seconds BETWEEN 10 AND 86400),
    destination_id    uuid NOT NULL,
    retention_days    int NOT NULL CHECK (retention_days BETWEEN 1 AND 30),
    enabled           boolean NOT NULL DEFAULT true,
    next_poll_at      timestamptz NOT NULL DEFAULT now(),
    last_polled_at    timestamptz,
    last_error        text,
    created_at        timestamptz NOT NULL DEFAULT now(),
    UNIQUE (organization_id, id),
    FOREIGN KEY (organization_id, project_id, environment_id) REFERENCES environments (organization_id, project_id, id),
    FOREIGN KEY (organization_id, project_id, environment_id, destination_id)
        REFERENCES webhook_destinations (organization_id, project_id, environment_id, id)
);
CREATE INDEX ix_poll_sources_due ON poll_sources (next_poll_at) WHERE enabled;

-- Observed state: one row per (source, stable record id) with the hash of its last seen content.
CREATE TABLE poll_observed_records (
    organization_id  uuid NOT NULL,
    source_id        uuid NOT NULL,
    record_key       text NOT NULL CHECK (length(record_key) BETWEEN 1 AND 512),
    content_hash     bytea NOT NULL,
    first_seen_at    timestamptz NOT NULL,
    last_seen_at     timestamptz NOT NULL,
    last_changed_at  timestamptz NOT NULL,
    PRIMARY KEY (source_id, record_key),
    FOREIGN KEY (organization_id, source_id) REFERENCES poll_sources (organization_id, id)
);

ALTER TABLE poll_sources ENABLE ROW LEVEL SECURITY;
ALTER TABLE poll_observed_records ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON poll_sources TO relaystack_app
    USING (organization_id = app_current_org()) WITH CHECK (organization_id = app_current_org());
CREATE POLICY worker_access ON poll_sources TO relaystack_worker USING (true) WITH CHECK (true);
CREATE POLICY tenant_isolation ON poll_observed_records TO relaystack_app
    USING (organization_id = app_current_org()) WITH CHECK (organization_id = app_current_org());
CREATE POLICY worker_access ON poll_observed_records TO relaystack_worker USING (true) WITH CHECK (true);

GRANT SELECT, INSERT, UPDATE ON poll_sources TO relaystack_app;
GRANT SELECT ON poll_observed_records TO relaystack_app;
GRANT SELECT, UPDATE ON poll_sources TO relaystack_worker;
GRANT SELECT, INSERT, UPDATE ON poll_observed_records TO relaystack_worker;
