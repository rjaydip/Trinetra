-- ===========================================================================
-- v1.17 — authenticated credential test for an already-registered camera
-- ===========================================================================
--
-- The registration flow changed: "Test connection" on the register-camera
-- page now creates the camera immediately (POST /api/v1/cameras) with the
-- minimal fields collected so far, saves the operator-entered username and
-- password through the existing PUT /cameras/{id}/credential mechanism, and
-- only then runs a REAL authenticated probe against the device. The rest of
-- the registration form becomes an edit of that camera. This version adds the
-- job table the new probe writes to.
--
-- camera_credential_test is deliberately its own table, not an extension of
-- camera_connection_test (v1.16): that table has no camera_id (it exists to
-- test host:port *before* a camera row exists) and its result shape
-- (CameraReachabilityReport) is TCP-only. This one always names an existing
-- camera, resolves and uses its stored credential, and its result shape
-- carries an authentication outcome, not just reachability.
--
-- No vendor adapter is involved (Federation.Adapters is Model 3 / VMS-only).
-- The prober (Federation.Runtime CameraCredentialProbe) is protocol-aware in
-- a narrow, honest way: HTTP/HTTPS/ONVIF gets a real HTTP Basic-auth request;
-- RTSP/RTSPS gets an RFC 2326 OPTIONS handshake with Basic/Digest; RTMP/SRT/
-- OTHER degrade to a reachability-only result with an explicit
-- "not verifiable" outcome rather than ever claiming a credential was
-- checked when it was not.
--
-- Requires: v1.sql .. v1.16.sql
-- ===========================================================================

SET search_path = federation, public;

CREATE TABLE IF NOT EXISTS camera_credential_test (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    camera_id           UUID NOT NULL REFERENCES cameras(id) ON DELETE CASCADE,

    -- Snapshot of what was tested, taken at claim time. The camera row can be
    -- edited after the test is claimed but before the job runs; the job must
    -- test what the operator saw when they clicked Test, not whatever the
    -- row has drifted to by the time a background task picks it up.
    protocol            VARCHAR(50) NOT NULL
                        CHECK (protocol IN
                            ('RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER')),
    ip_address           INET NOT NULL,
    port                 INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
    credential_reference TEXT NOT NULL,

    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending','running','completed','failed','abandoned')),

    requested_by        TEXT NOT NULL,
    requested_at         TIMESTAMPTZ NOT NULL DEFAULT now(),

    executed_by          TEXT,
    started_at           TIMESTAMPTZ,
    completed_at          TIMESTAMPTZ,

    -- CameraCredentialTestReport (Trinetra.Federation.Runtime): reachable,
    -- authOutcome ("authenticated" | "credential_rejected" | "not_verifiable"
    -- | "unreachable" | "error"), connectMs, detail, failure.
    result               JSONB,
    failure_reason        TEXT
);

CREATE INDEX IF NOT EXISTS ix_camera_credential_test_camera
    ON camera_credential_test (camera_id, requested_at DESC);

-- The sweeper's access path: jobs still claimed to be running.
CREATE INDEX IF NOT EXISTS ix_camera_credential_test_active
    ON camera_credential_test (started_at)
    WHERE status IN ('pending', 'running');

CREATE INDEX IF NOT EXISTS ix_camera_credential_test_requested
    ON camera_credential_test (requested_at DESC);

-- Same shape as sweep_abandoned_camera_connection_tests (v1.16), one table over.
CREATE OR REPLACE FUNCTION sweep_abandoned_camera_credential_tests(
    stale_after INTERVAL DEFAULT '5 minutes')
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    swept INTEGER;
BEGIN
    UPDATE camera_credential_test
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

-- Low-volume table, same reasoning as purge_camera_connection_tests_before:
-- DELETE is fine, no partitioning needed. Shares the ConnectionTests
-- retention cutoff (RetentionOptions) -- same category of ephemeral job row.
CREATE OR REPLACE FUNCTION purge_camera_credential_tests_before(cutoff TIMESTAMPTZ)
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    removed INTEGER;
BEGIN
    DELETE FROM camera_credential_test
    WHERE requested_at < cutoff
      AND status IN ('completed', 'failed', 'abandoned');

    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END $$;
