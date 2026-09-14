-- ===========================================================================
-- v1.18 — Correlation: windowed cross-camera matching (ARCHITECTURE-MODEL-3.md §8, Gap 1)
-- ===========================================================================
--
-- Day-one correlation is windowed SQL polling over federation_event, behind the
-- ICorrelationEngine port (SqlCorrelationEngine in Trinetra.Federation.Storage) --
-- NOT Kafka. correlation_rule holds the per-rule config (time window, spatial
-- radius, confidence floor, required signal agreement) so operators can tune
-- without a deployment; it is system-seeded here and has no write API in this
-- pass. correlation_group is the cluster a rule found; correlation_group_member
-- is its join table, since a group can have more than two members.
--
-- Idempotency (CLAUDE.md #5): the unique constraint is on
-- (rule_id, natural_key, window_bucket), not on the group's own id, so
-- SqlCorrelationEngine re-running an overlapping window on restart hits
-- ON CONFLICT DO NOTHING rather than creating a duplicate group.
--
-- correlation_group_member denormalises organization_unit_id / geographic_area_id
-- (and camera_id / source_vms_id) from federation_event onto every member row --
-- the same reasoning federation_event itself documents: correlation must not
-- join back to a partitioned, 100-400M-rows/day table to resolve scope. It
-- also means the API's scoped read (CorrelationRepository) never touches
-- federation_event at all. federation_event_id is stored rather than an FK:
-- federation_event's primary key is (occurred_at, event_id) because it is
-- RANGE partitioned on occurred_at, so a real FK would need the partition key
-- carried here too for no benefit -- event_occurred_at is kept alongside it
-- for that same reason, as a plain column, not a constraint.
--
-- New permission correlation.read, granted like worker.read in v1.13: SUPER_ADMIN
-- (via the unconditional CROSS JOIN backfill), STATE_ADMIN, DEPARTMENT_ADMIN.
-- No write endpoint and no permission for one in this pass.
--
-- Requires: v1.sql .. v1.17.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- correlation_rule — configuration, not code (architecture §8)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS correlation_rule (
    id                         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code                       TEXT NOT NULL UNIQUE,
    name                       TEXT NOT NULL,
    description                TEXT,

    -- Events sharing an object_reference are bucketed into windows of this width;
    -- only events in the same bucket can join one group.
    time_window_seconds        INTEGER NOT NULL CHECK (time_window_seconds > 0),

    -- Plain-trig haversine radius against the bucket's centroid -- no PostGIS
    -- (CLAUDE.md: no PostgreSQL extensions). NULL disables the spatial check.
    spatial_radius_meters      DOUBLE PRECISION CHECK (spatial_radius_meters IS NULL OR spatial_radius_meters > 0),

    -- Minimum average member confidence for a candidate group to be recorded.
    confidence_floor           DOUBLE PRECISION NOT NULL
                                CHECK (confidence_floor >= 0 AND confidence_floor <= 1),

    -- Minimum number of distinct cameras that must agree before a group forms.
    -- A single camera's own repeated observations never form a group alone.
    required_signal_agreement  INTEGER NOT NULL DEFAULT 2 CHECK (required_signal_agreement >= 2),

    is_active                  BOOLEAN NOT NULL DEFAULT TRUE,
    created_at                 TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at                 TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE correlation_rule IS
    'Per-rule correlation config: time window, spatial radius, confidence floor, required '
    'signal agreement (architecture §8). System-seeded; no write API in this pass -- edited by '
    'a future admin route, not by a deployment.';

-- ---------------------------------------------------------------------------
-- correlation_group — one cluster a rule found
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS correlation_group (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rule_id           UUID NOT NULL REFERENCES correlation_rule(id),

    -- The federation_event.object_reference the group formed on: a plate, a
    -- person reference, a track id.
    natural_key       TEXT NOT NULL,

    -- Start of the time bucket this group was computed for.
    window_bucket     TIMESTAMPTZ NOT NULL,

    -- Possible-match score in [0,1]. NEVER certainty -- cross-camera identity is
    -- probabilistic (CLAUDE.md production posture); every surface presenting
    -- this must say "possible match", never "confirmed".
    confidence        DOUBLE PRECISION NOT NULL CHECK (confidence >= 0 AND confidence <= 1),

    member_count      INTEGER NOT NULL CHECK (member_count >= 2),
    first_occurred_at TIMESTAMPTZ NOT NULL,
    last_occurred_at  TIMESTAMPTZ NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Idempotency key (CLAUDE.md #5): NOT on the group's own id, so a restart or
    -- an overlapping run of SqlCorrelationEngine over the same window hits
    -- ON CONFLICT DO NOTHING instead of creating a duplicate group.
    CONSTRAINT uq_correlation_group_dedup UNIQUE (rule_id, natural_key, window_bucket)
);

COMMENT ON TABLE correlation_group IS
    'One cluster of events an ICorrelationEngine run judged a possible match. confidence is '
    'always a possible-match score, never certainty. Deduplicated on '
    '(rule_id, natural_key, window_bucket), not on id.';

-- The API's list access path: recent groups, newest activity first.
CREATE INDEX IF NOT EXISTS ix_correlation_group_last_occurred
    ON correlation_group (last_occurred_at DESC);

-- ---------------------------------------------------------------------------
-- correlation_group_member — join table; a group can have more than 2 members
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS correlation_group_member (
    group_id             UUID NOT NULL REFERENCES correlation_group(id) ON DELETE CASCADE,

    -- federation_event.event_id -- not an FK. federation_event is RANGE
    -- partitioned on occurred_at and its primary key is (occurred_at, event_id),
    -- so a real FK would need the partition key carried here too for no
    -- benefit; event_occurred_at below is kept as a plain column for that
    -- reason, alongside the scope columns every event query already denormalises.
    federation_event_id  TEXT NOT NULL,
    event_occurred_at    TIMESTAMPTZ NOT NULL,

    camera_id             TEXT NOT NULL,
    source_vms_id          UUID NOT NULL,

    -- Denormalised from federation_event so the API's scoped read
    -- (CorrelationRepository) never joins back to the 100-400M-rows/day event
    -- table -- the same reasoning federation_event itself documents for why it
    -- denormalises organization_unit_id off the camera.
    organization_unit_id UUID NOT NULL,
    geographic_area_id   UUID,

    PRIMARY KEY (group_id, federation_event_id)
);

COMMENT ON TABLE correlation_group_member IS
    'Join table: the federation_event rows that make up one correlation_group. '
    'organization_unit_id / geographic_area_id are denormalised from federation_event so a '
    'scoped read never joins back to the partitioned event table.';

-- The scoped-read access path: "does this group have a member this caller can reach".
CREATE INDEX IF NOT EXISTS ix_correlation_member_org
    ON correlation_group_member (group_id, organization_unit_id);
CREATE INDEX IF NOT EXISTS ix_correlation_member_geo
    ON correlation_group_member (group_id, geographic_area_id)
    WHERE geographic_area_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Permission — same shape as worker.read (v1.13)
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('correlation.read', 'View correlation groups', 'events',
        'List cross-camera possible-match groups and their member events')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN holds every permission -- unconditional, idempotent backfill, the
-- same one v1.4 / v1.11 / v1.12 / v1.13 use.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN',      'correlation.read'),
    ('DEPARTMENT_ADMIN', 'correlation.read')
)
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- Seed rule — the ANPR cross-camera scenario the demo path needs
-- ---------------------------------------------------------------------------

INSERT INTO correlation_rule
    (code, name, description, time_window_seconds, spatial_radius_meters,
     confidence_floor, required_signal_agreement, is_active)
VALUES
    ('ANPR_CROSS_CAMERA', 'ANPR cross-camera match',
     'Same plate seen by two or more cameras within a 10-minute window. No spatial radius -- '
     'plate movement between camera sites is exactly the signal this rule looks for.',
     600, NULL, 0.6, 2, TRUE)
ON CONFLICT (code) DO NOTHING;
