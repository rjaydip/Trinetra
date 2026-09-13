-- ===========================================================================
-- v1.16 — camera event-recording flag + standalone reachability probe
-- ===========================================================================
--
-- Two independent additions, both scoped to the standalone camera registry
-- (cameras), not Model 3 federation targets:
--
--   1. cameras.record_events — a plain operator-set flag controlling whether
--      this camera's future event stream should be persisted once Model 2/3
--      start writing events for registry cameras. Not derived from anything;
--      defaults true so existing and newly registered cameras keep their
--      current (implicit) behaviour unless explicitly turned off.
--
--   2. camera_connection_test — a job table for a pre-save reachability probe
--      (POST /api/v1/cameras/connection-test), mirroring connection_test's
--      async create-then-poll shape (v1.sql) but for a camera that does not
--      exist as a row yet, so it cannot carry a camera_id FK. No credential,
--      no vendor adapter: the probe (Federation.Runtime) opens a plain TCP
--      socket to ip_address:port with a short timeout and reports whether it
--      opened — nothing about the device beyond that. Federation.Adapters is
--      not involved, matching CLAUDE.md's "adapters are the only place
--      vendor-specific code lives": this deliberately has none.
--
--      Retention reuses the existing `ConnectionTests` cutoff
--      (RetentionOptions) — same category of ephemeral job row, not worth a
--      second knob for.
--
-- Requires: v1.sql .. v1.15.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- 1. cameras.record_events
-- ---------------------------------------------------------------------------

ALTER TABLE cameras
    ADD COLUMN IF NOT EXISTS record_events BOOLEAN NOT NULL DEFAULT true;

COMMENT ON COLUMN cameras.record_events IS
    'Operator-set flag: whether this camera''s future event stream should be '
    'persisted. Plain column, not derived from anything else on the row. '
    'Defaults true so registering a camera keeps the current implicit '
    'behaviour unless the operator turns it off.';

-- ---------------------------------------------------------------------------
-- 2. camera_connection_test — pre-save reachability probe for standalone cameras
-- ---------------------------------------------------------------------------
--
-- No camera_id: the whole point is to test host:port before a camera row
-- exists to attach it to. requested_by/executed_by follow connection_test's
-- shape (TEXT actor label, not a FK) for the same reason — a test can outlive
-- the session that started it.

CREATE TABLE IF NOT EXISTS camera_connection_test (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    ip_address      INET NOT NULL,
    port            INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
    protocol        VARCHAR(50) NOT NULL
                    CHECK (protocol IN
                        ('RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER')),

    status          VARCHAR(20) NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending','running','completed','failed','abandoned')),

    requested_by    TEXT NOT NULL,
    requested_at    TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Which instance picked the job up. Needed to recognise work abandoned by a dead instance.
    executed_by     TEXT,
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,

    result          JSONB,
    failure_reason  TEXT
);

-- The sweeper's access path: jobs still claimed to be running.
CREATE INDEX IF NOT EXISTS ix_camera_connection_test_active
    ON camera_connection_test (started_at)
    WHERE status IN ('pending', 'running');

CREATE INDEX IF NOT EXISTS ix_camera_connection_test_requested
    ON camera_connection_test (requested_at DESC);

-- Marks jobs abandoned when the executing instance died mid-test. Same shape as
-- sweep_abandoned_connection_tests (v1.sql), one table over.
CREATE OR REPLACE FUNCTION sweep_abandoned_camera_connection_tests(
    stale_after INTERVAL DEFAULT '5 minutes')
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    swept INTEGER;
BEGIN
    UPDATE camera_connection_test
    SET status = 'abandoned',
        completed_at = now(),
        failure_reason = format(
            'No result after %s. The API instance running this test most likely stopped.',
            stale_after)
    WHERE status IN ('pending', 'running')
      AND COALESCE(started_at, requested_at) < now() - stale_after;

    GET DIAGNOSTICS swept = ROW_COUNT;
    RETURN swept;
END $$;

-- Low-volume table, same reasoning as purge_connection_tests_before: DELETE is
-- fine, no partitioning needed.
CREATE OR REPLACE FUNCTION purge_camera_connection_tests_before(cutoff TIMESTAMPTZ)
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    removed INTEGER;
BEGIN
    DELETE FROM camera_connection_test
    WHERE requested_at < cutoff
      AND status IN ('completed', 'failed', 'abandoned');

    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END $$;
