-- Worker tenant scoping (ADR-014).
--
-- Before this migration the worker role had permissive "USING (true)" policies and could read and write every
-- tenant's rows; isolation in the worker depended on every query filtering by hand. Now:
--
--   * The worker role is subject to the same organization policy as the API role. A worker connection only sees
--     rows of the organization in relaystack.organization_id, and sees nothing when it is unset (fail closed).
--   * Finding work across tenants goes through the SECURITY DEFINER functions below. Each one leases a bounded
--     number of due rows and returns only those rows. The worker then handles each item in a connection scoped to
--     that item's organization.
--
-- The functions are owned by the migration role (the table owner, to which RLS does not apply) and are executable
-- only by relaystack_worker.

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['organizations', 'projects', 'environments', 'api_keys', 'webhook_destinations', 'inboxes',
                             'files', 'validation_errors', 'jobs', 'events', 'deliveries', 'delivery_attempts',
                             'usage_records', 'audit_log', 'poll_sources', 'poll_observed_records']
    LOOP
        EXECUTE format('DROP POLICY worker_access ON %I', t);
        EXECUTE format('ALTER POLICY tenant_isolation ON %I TO relaystack_app, relaystack_worker', t);
    END LOOP;
END
$$;

-- Leases due jobs, including jobs whose lease expired because their worker crashed.
CREATE FUNCTION worker_claim_jobs(p_worker text, p_max int, p_lease_until timestamptz, p_now timestamptz)
    RETURNS SETOF jobs
    LANGUAGE sql VOLATILE SECURITY DEFINER
    SET search_path = public, pg_temp
AS $$
    UPDATE jobs j
    SET status = 'running', locked_by = p_worker, locked_until = p_lease_until, attempts = j.attempts + 1
    FROM (
        SELECT id FROM jobs
        WHERE (status = 'pending' AND run_after <= p_now)
           OR (status = 'running' AND locked_until <= p_now)
        ORDER BY run_after
        LIMIT least(greatest(p_max, 0), 100)
        FOR UPDATE SKIP LOCKED
    ) c
    WHERE j.id = c.id
    RETURNING j.*
$$;

-- Leases due deliveries while keeping each organization at or below p_max_per_org in-flight deliveries.
-- The cap is soft under concurrent dispatchers (see DELIVERY_SEMANTICS.md).
CREATE FUNCTION worker_claim_deliveries(p_worker text, p_batch int, p_max_per_org int, p_lease_until timestamptz, p_now timestamptz)
    RETURNS SETOF deliveries
    LANGUAGE sql VOLATILE SECURITY DEFINER
    SET search_path = public, pg_temp
AS $$
    WITH inflight AS (
        SELECT organization_id, count(*) AS n FROM deliveries
        WHERE status = 'in_flight' AND locked_until > p_now
        GROUP BY organization_id
    ),
    candidates AS (
        SELECT d.id,
               row_number() OVER (PARTITION BY d.organization_id ORDER BY d.next_attempt_at, d.id) AS rn,
               coalesce(i.n, 0) AS busy
        FROM deliveries d
        LEFT JOIN inflight i ON i.organization_id = d.organization_id
        WHERE (d.status = 'pending' AND d.next_attempt_at <= p_now)
           OR (d.status = 'in_flight' AND d.locked_until <= p_now)
        ORDER BY d.next_attempt_at
        LIMIT least(greatest(p_batch, 0), 500) * 10
    )
    UPDATE deliveries d
    SET status = 'in_flight', locked_by = p_worker, locked_until = p_lease_until, attempt_count = d.attempt_count + 1
    FROM (
        SELECT x.id FROM deliveries x
        WHERE x.id IN (SELECT id FROM candidates WHERE rn + busy <= p_max_per_org)
          AND ((x.status = 'pending' AND x.next_attempt_at <= p_now) OR (x.status = 'in_flight' AND x.locked_until <= p_now))
        ORDER BY x.next_attempt_at
        LIMIT least(greatest(p_batch, 0), 500)
        FOR UPDATE SKIP LOCKED
    ) c
    WHERE d.id = c.id
    RETURNING d.*
$$;

-- Advancing next_poll_at is the claim. A crash mid-poll means the source is polled again next interval.
CREATE FUNCTION worker_claim_poll_sources(p_max int, p_now timestamptz)
    RETURNS SETOF poll_sources
    LANGUAGE sql VOLATILE SECURITY DEFINER
    SET search_path = public, pg_temp
AS $$
    UPDATE poll_sources p SET next_poll_at = p_now + make_interval(secs => p.interval_seconds)
    FROM (
        SELECT id FROM poll_sources
        WHERE enabled AND next_poll_at <= p_now
        ORDER BY next_poll_at
        LIMIT least(greatest(p_max, 0), 50)
        FOR UPDATE SKIP LOCKED
    ) c
    WHERE p.id = c.id
    RETURNING p.*
$$;

-- Organizations that have files or event payloads past retention. Returns identifiers only; the sweep itself
-- runs per organization under RLS.
CREATE FUNCTION worker_orgs_with_expired_payloads(p_now timestamptz, p_max int)
    RETURNS SETOF uuid
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path = public, pg_temp
AS $$
    SELECT organization_id FROM (
        SELECT organization_id FROM files
        WHERE expires_at <= p_now AND status NOT IN ('expired', 'received', 'processing')
        UNION
        SELECT organization_id FROM events
        WHERE expires_at <= p_now AND purged_at IS NULL
    ) x
    LIMIT least(greatest(p_max, 0), 1000)
$$;

REVOKE ALL ON FUNCTION worker_claim_jobs(text, int, timestamptz, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION worker_claim_deliveries(text, int, int, timestamptz, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION worker_claim_poll_sources(int, timestamptz) FROM PUBLIC;
REVOKE ALL ON FUNCTION worker_orgs_with_expired_payloads(timestamptz, int) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION worker_claim_jobs(text, int, timestamptz, timestamptz) TO relaystack_worker;
GRANT EXECUTE ON FUNCTION worker_claim_deliveries(text, int, int, timestamptz, timestamptz) TO relaystack_worker;
GRANT EXECUTE ON FUNCTION worker_claim_poll_sources(int, timestamptz) TO relaystack_worker;
GRANT EXECUTE ON FUNCTION worker_orgs_with_expired_payloads(timestamptz, int) TO relaystack_worker;
