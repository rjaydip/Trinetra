-- ===========================================================================
-- v1.6 — Centralised CCTV Registry & GIS Mapping
-- ===========================================================================
--
-- Adds the AUTHORITATIVE camera registry. Until now the only camera rows in the
-- system were federation.federated_camera — an OBSERVATION of what a VMS
-- currently reports, keyed by (target_id, native_camera_id), never
-- authoritative, camera_id always NULL because the registry did not exist.
--
-- This version adds the registry itself:
--
--   cameras                 one row per registered CCTV asset. Ownership
--                           (organization_unit_id) and location (site_id) are
--                           independent dimensions — CAMERA-SCHEMA.md's core
--                           rule — and the hierarchy is resolved through the
--                           referenced rows, not duplicated here.
--   camera_health_history   health-status transitions (manual overrides now;
--                           a bus-driven writer later). Not partitioned: unlike
--                           the per-poll camera_status_history, this table
--                           records transitions only and stays small.
--   maintenance_records     maintenance lifecycle per camera. A record moving
--                           to IN_PROGRESS / COMPLETED is what drives the
--                           camera's maintenance_status.
--
-- GEOMETRY DECISION
--
--   No PostGIS, no geometry column. Consistent with CLAUDE.md ("No PostgreSQL
--   extensions") and with federated_camera / sites. Coverage sectors are pure
--   trigonometry over DECIMAL lat/long, computed in the application on read.
--   Coverage-GAP analysis (ST_Union / ST_Difference over boundary polygons) is
--   deferred to the version that introduces spatial querying.
--
-- RECONCILIATION
--
--   federated_camera.camera_id (nullable, no FK) is the link. It is set by
--   POST /api/v1/cameras/{id}/reconcile and never cleared by the inventory
--   poll (the connector inventory merge omits it from ON CONFLICT DO UPDATE).
--   No federation-side schema change is needed here.
--
-- Requires: v1.sql, v1.1.sql, v1.2.sql, v1.3.sql, v1.4.sql, v1.5.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- cameras — the authoritative registry record
-- ---------------------------------------------------------------------------
--
-- Units: azimuth / tilt / *_fov in degrees; effective_range / *_height /
-- altitude in metres. Enumerations are VARCHAR + CHECK (matches sites,
-- geographic_areas, CAMERA-SCHEMA.md) rather than new ENUM types.

CREATE TABLE IF NOT EXISTS cameras (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    camera_code           VARCHAR(100) NOT NULL,
    name                  VARCHAR(255) NOT NULL,

    -- The two independent dimensions. Both required: a registered camera always
    -- has an owner and a place. (federated_camera.site_id is nullable because a
    -- VMS may report a camera before it is sited; the registry does not.)
    organization_unit_id  UUID NOT NULL REFERENCES organization_units(id),
    site_id               UUID NOT NULL REFERENCES sites(id),

    -- No vendors table yet (vendor_kind is a federation-side enum, not an entity).
    -- Vendor is free text for now; a vendor entity is a later change.
    manufacturer          VARCHAR(255),
    model                 VARCHAR(255),
    camera_type           VARCHAR(50) NOT NULL
                          CHECK (camera_type IN
                              ('FIXED','PTZ','DOME','BULLET','ANPR','THERMAL','MULTISENSOR','OTHER')),
    serial_number         VARCHAR(255),

    latitude              DECIMAL(10,7) NOT NULL CHECK (latitude  BETWEEN -90  AND 90),
    longitude             DECIMAL(10,7) NOT NULL CHECK (longitude BETWEEN -180 AND 180),
    altitude              DECIMAL(10,3) CHECK (altitude BETWEEN -500 AND 9000),
    mounting_height       DECIMAL(10,3) CHECK (mounting_height BETWEEN 0 AND 200),

    azimuth               DECIMAL(7,3) CHECK (azimuth        >= 0   AND azimuth        <  360),
    tilt                  DECIMAL(7,3) CHECK (tilt           >= -90 AND tilt           <= 90),
    horizontal_fov        DECIMAL(7,3) CHECK (horizontal_fov >  0   AND horizontal_fov <= 360),
    vertical_fov          DECIMAL(7,3) CHECK (vertical_fov   >  0   AND vertical_fov   <= 180),
    effective_range       DECIMAL(10,3) CHECK (effective_range > 0  AND effective_range <= 5000),

    ip_address            INET,
    port                  INTEGER CHECK (port BETWEEN 1 AND 65535),
    protocol              VARCHAR(50)
                          CHECK (protocol IS NULL OR protocol IN
                              ('RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER')),

    -- The connector_target.id this camera is served by. No FK: a registry camera may name a VMS that
    -- is later removed, mirroring federated_camera.camera_id having no FK back.
    vms_id                UUID,
    stream_reference      VARCHAR(512),
    -- Opaque pointer into the secret layer. The registry never resolves it.
    credential_reference  VARCHAR(255),

    installation_date     DATE,

    operational_status    VARCHAR(30) NOT NULL DEFAULT 'UNKNOWN'
                          CHECK (operational_status IN ('ONLINE','OFFLINE','DEGRADED','UNKNOWN')),
    connectivity_status   VARCHAR(30) NOT NULL DEFAULT 'UNKNOWN'
                          CHECK (connectivity_status IN ('CONNECTED','DISCONNECTED','UNKNOWN')),
    maintenance_status    VARCHAR(30) NOT NULL DEFAULT 'NORMAL'
                          CHECK (maintenance_status IN
                              ('NORMAL','REQUIRED','UNDER_MAINTENANCE','RETIRED')),

    last_seen_at          TIMESTAMPTZ,
    last_health_check_at  TIMESTAMPTZ,

    -- Soft delete / retire. The row stays for the sake of historical
    -- observations and reconciliation links.
    deleted_at            TIMESTAMPTZ,
    deleted_by            UUID,

    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by            UUID NOT NULL,
    updated_by            UUID NOT NULL,

    -- RETIRED and the retirement stamp travel together, both directions.
    CONSTRAINT ck_cameras_retire_consistent CHECK (
        (maintenance_status = 'RETIRED') = (deleted_at IS NOT NULL)
    )
);

-- camera_code is unique among LIVE rows only: retiring a camera frees its code
-- for reuse, and a partial unique index is how that is expressed.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cameras_code_live
    ON cameras (camera_code) WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_cameras_organization_unit ON cameras (organization_unit_id);
CREATE INDEX IF NOT EXISTS ix_cameras_site              ON cameras (site_id);
CREATE INDEX IF NOT EXISTS ix_cameras_camera_type       ON cameras (camera_type);
CREATE INDEX IF NOT EXISTS ix_cameras_operational_status ON cameras (operational_status);
CREATE INDEX IF NOT EXISTS ix_cameras_maintenance_status ON cameras (maintenance_status);
CREATE INDEX IF NOT EXISTS ix_cameras_vms               ON cameras (vms_id) WHERE vms_id IS NOT NULL;
-- Bounding-box scans for the GIS feed, and keyset list ordering, over live rows.
CREATE INDEX IF NOT EXISTS ix_cameras_bbox_live         ON cameras (latitude, longitude)
    WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------------------
-- camera_health_history — status transitions
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS camera_health_history (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    camera_id           UUID NOT NULL REFERENCES cameras(id) ON DELETE CASCADE,

    operational_status  VARCHAR(30) NOT NULL
                        CHECK (operational_status IN ('ONLINE','OFFLINE','DEGRADED','UNKNOWN')),
    connectivity_status VARCHAR(30) NOT NULL
                        CHECK (connectivity_status IN ('CONNECTED','DISCONNECTED','UNKNOWN')),

    checked_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    latency_ms          INTEGER CHECK (latency_ms >= 0),
    error_code          VARCHAR(100),
    failure_reason      TEXT,
    source              VARCHAR(20) NOT NULL DEFAULT 'MANUAL'
                        CHECK (source IN ('MANUAL','FEDERATION','AI_WORKER','PROBE')),
    details             JSONB NOT NULL DEFAULT '{}'::jsonb,
    recorded_by         UUID
);

CREATE INDEX IF NOT EXISTS ix_camera_health_history_camera
    ON camera_health_history (camera_id, checked_at DESC);

-- ---------------------------------------------------------------------------
-- maintenance_records — maintenance lifecycle
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS maintenance_records (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    camera_id         UUID NOT NULL REFERENCES cameras(id) ON DELETE CASCADE,

    maintenance_type  VARCHAR(40) NOT NULL
                      CHECK (maintenance_type IN
                          ('PREVENTIVE','CORRECTIVE','INSPECTION','INSTALLATION',
                           'RELOCATION','DECOMMISSION','OTHER')),
    status            VARCHAR(20) NOT NULL DEFAULT 'OPEN'
                      CHECK (status IN ('OPEN','IN_PROGRESS','COMPLETED','CANCELLED')),

    description       TEXT NOT NULL,
    failure_reason    TEXT,

    reported_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at        TIMESTAMPTZ,
    completed_at      TIMESTAMPTZ,
    next_due_at       TIMESTAMPTZ,

    -- Free text: maintenance is often done by an external contractor, not a
    -- platform user.
    performed_by      VARCHAR(255),

    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by        UUID NOT NULL,
    updated_by        UUID NOT NULL,

    CONSTRAINT ck_maintenance_records_completed_at CHECK (
        (status = 'COMPLETED') = (completed_at IS NOT NULL)
    ),
    CONSTRAINT ck_maintenance_records_time_order CHECK (
        started_at IS NULL OR started_at >= reported_at
    )
);

CREATE INDEX IF NOT EXISTS ix_maintenance_records_camera
    ON maintenance_records (camera_id, reported_at DESC);
CREATE INDEX IF NOT EXISTS ix_maintenance_records_open
    ON maintenance_records (camera_id) WHERE status IN ('OPEN','IN_PROGRESS');
CREATE INDEX IF NOT EXISTS ix_maintenance_records_due
    ON maintenance_records (next_due_at)
    WHERE status <> 'CANCELLED' AND next_due_at IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Permissions
-- ---------------------------------------------------------------------------
--
-- camera.read / camera.create / camera.update / camera.health.read /
-- camera.maintenance.* / gis.read / gis.coverage.read already exist (v1.sql).
-- These three are new: retire, bulk import and reconciliation are each distinct
-- enough from "edit a camera" to be delegated or withheld on their own.

INSERT INTO permissions (code, name, category, description) VALUES
    ('camera.delete',    'Retire cameras',      'camera', 'Soft-delete / retire camera registry entries'),
    ('camera.import',    'Bulk import cameras', 'camera', 'Create cameras in bulk from an import batch'),
    ('camera.reconcile', 'Reconcile cameras',   'camera', 'Link registry cameras to VMS-discovered cameras')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN gets every permission, including these. The CROSS JOIN only runs
-- when its own file runs (v1.4 exists because this was once missed), so v1.6
-- must repeat it.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r CROSS JOIN permissions p WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    -- New registry admin actions.
    ('STATE_ADMIN','camera.delete'), ('STATE_ADMIN','camera.import'), ('STATE_ADMIN','camera.reconcile'),
    ('DEPARTMENT_ADMIN','camera.delete'), ('DEPARTMENT_ADMIN','camera.import'),
    ('DEPARTMENT_ADMIN','camera.reconcile'),

    -- VMS_ADMIN is the reconciliation user; from-federated also needs camera.create.
    ('VMS_ADMIN','camera.reconcile'), ('VMS_ADMIN','camera.create'),

    -- v1.sql gave maintenance to MAINTENANCE_OPERATOR only, and camera.health.read
    -- to everyone except STATE_ADMIN. Close both gaps so the admin roles can use
    -- the health and maintenance endpoints.
    ('STATE_ADMIN','camera.health.read'),
    ('STATE_ADMIN','camera.maintenance.read'), ('STATE_ADMIN','camera.maintenance.update'),
    ('DEPARTMENT_ADMIN','camera.maintenance.read'), ('DEPARTMENT_ADMIN','camera.maintenance.update')
)
ON CONFLICT DO NOTHING;
