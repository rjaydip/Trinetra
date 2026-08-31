-- ===========================================================================
-- v1.2 — Detection ingest, watchlist and AI-worker health
-- ===========================================================================
--
-- Adds the storage side for Model 2's capture-and-inference worker
-- (ai-worker/, Python): the sink for POST /api/v1/detections, a watchlist
-- keyed on plate number that ingest matches against, and a small table for
-- the worker's own heartbeat.
--
-- WHY A SEPARATE detection_event RATHER THAN REUSING federation_event
--
-- federation_event is Model 3's normalised VMS-event stream, keyed on
-- (source_vms_id, source_event_id) — a VMS's own notion of an event.
-- Detections come from Model 2's inference, not from a VMS, and carry a
-- different shape (vehicle/plate attributes, no vendor_event_type). Folding
-- them into federation_event would mean either nullable vendor-only columns
-- on every VMS event, or optional detection columns on every VMS row —
-- both worse than a second table with the same partitioning shape.
--
-- WHY NO DAILY PARTITION-MAINTENANCE FUNCTION (unlike camera_status_history)
--
-- Detection volume at the current rollout size (100+ cameras, 2 fps sampled)
-- does not approach federation_event's 100-400M rows/day scale yet. A single
-- DEFAULT partition is enough for now; range partitioning by occurred_at is
-- still declared so a time-bounded partition scheme can be dropped in later
-- without a table rewrite, exactly as federation_event already does.
--
-- Requires: v1.sql, v1.1.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Detection ingest
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS detection_event (
    event_id             TEXT NOT NULL,      -- worker-generated, e.g. "evt-<uuid4hex>"
    occurred_at          TIMESTAMPTZ NOT NULL,

    target_id            UUID NOT NULL REFERENCES connector_target(id) ON DELETE CASCADE,
    native_camera_id     TEXT NOT NULL,
    -- Model 1's registry id. NULL until reconciliation matches this camera to a registered
    -- one — same convention as federated_camera.camera_id.
    camera_id            UUID,

    -- Denormalised deliberately, matching federation_event: scoping a query must not join to
    -- resolve ownership.
    organization_unit_id UUID NOT NULL REFERENCES organization_units(id),
    site_id              UUID REFERENCES sites(id),

    event_type           TEXT NOT NULL,      -- "VEHICLE_DETECTED" | "ANPR_DETECTED"
    confidence           DOUBLE PRECISION
                         CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),

    vehicle_type          TEXT,
    plate_number_raw       TEXT,
    plate_number_normalized TEXT,

    -- An opaque reference, never inlined image bytes — Model 3's "video is source, metadata
    -- is product" rule applies here too. A relative disk path today; whatever the evidence
    -- storage location resolves to.
    snapshot_reference    TEXT,

    received_at           TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (occurred_at, event_id)
) PARTITION BY RANGE (occurred_at);

-- Idempotency (CLAUDE.md non-negotiable #5: every sink idempotent on
-- (source_vms, source_event_id)). Transport is at-least-once; ON CONFLICT DO NOTHING makes
-- this sink effectively-once.
CREATE UNIQUE INDEX IF NOT EXISTS ux_detection_event_dedup
    ON detection_event (event_id, occurred_at);

CREATE INDEX IF NOT EXISTS ix_detection_camera_time
    ON detection_event (target_id, native_camera_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_detection_org_time
    ON detection_event (organization_unit_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_detection_plate
    ON detection_event (plate_number_normalized, occurred_at DESC)
    WHERE plate_number_normalized IS NOT NULL;

CREATE TABLE IF NOT EXISTS detection_event_default PARTITION OF detection_event DEFAULT;

-- ---------------------------------------------------------------------------
-- Watchlist
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS watchlist_entry (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_unit_id    UUID NOT NULL REFERENCES organization_units(id),
    plate_number_normalized TEXT NOT NULL,
    reason                  TEXT,
    severity                TEXT NOT NULL DEFAULT 'Medium'
                            CHECK (severity IN ('Low', 'Medium', 'High', 'Critical')),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by              UUID REFERENCES platform_users(id)
);

-- One active entry per plate per organization. A revoked/deactivated entry does not block a
-- new one being raised for the same plate later.
CREATE UNIQUE INDEX IF NOT EXISTS ux_watchlist_active
    ON watchlist_entry (organization_unit_id, plate_number_normalized)
    WHERE is_active;

CREATE TABLE IF NOT EXISTS watchlist_alert (
    id                     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    watchlist_entry_id     UUID NOT NULL REFERENCES watchlist_entry(id) ON DELETE CASCADE,
    detection_event_id     TEXT NOT NULL,
    detection_occurred_at  TIMESTAMPTZ NOT NULL,
    raised_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    acknowledged_at        TIMESTAMPTZ,
    acknowledged_by        UUID REFERENCES platform_users(id),

    FOREIGN KEY (detection_occurred_at, detection_event_id)
        REFERENCES detection_event (occurred_at, event_id) ON DELETE CASCADE
);

-- A detection is matched against the watchlist at most once — a retried/duplicate ingest
-- POST must not raise a second alert for the same match.
CREATE UNIQUE INDEX IF NOT EXISTS ux_watchlist_alert_dedup
    ON watchlist_alert (watchlist_entry_id, detection_event_id);

CREATE INDEX IF NOT EXISTS ix_watchlist_alert_raised
    ON watchlist_alert (raised_at DESC);

-- ---------------------------------------------------------------------------
-- AI-worker health
-- ---------------------------------------------------------------------------

-- Deliberately not worker_node: that table is the leased-connector-worker registry (Model 3's
-- own workers, claimed/released against connector targets). An AI worker owns a static camera
-- partition (WORKER_INDEX/WORKER_COUNT) rather than a lease, so it gets its own small table
-- instead of overloading a different lifecycle's schema.
CREATE TABLE IF NOT EXISTS ai_worker_health (
    worker_id           TEXT PRIMARY KEY,
    hostname             TEXT,
    first_seen_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_heartbeat_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- Permissions
-- ---------------------------------------------------------------------------

-- observation.read/observation.search and alert.read/alert.acknowledge already exist (v1.sql)
-- and are reused as-is for reading detections and watchlist alerts. Only the write-side
-- capabilities are genuinely new.
INSERT INTO permissions (code, name, category, description) VALUES
    ('observation.write', 'Submit observations', 'analytics', 'Ingest Model 2 detection events'),
    ('watchlist.manage',  'Manage watchlist',    'alerts',    'Create and deactivate watchlist entries'),
    ('worker.heartbeat',  'Report worker health','vms',       'Submit AI-worker heartbeats')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;
