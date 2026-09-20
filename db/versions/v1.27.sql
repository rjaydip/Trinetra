-- ===========================================================================
-- v1.27 — dynamic camera<->AI-worker lease assignment
-- ===========================================================================
--
-- Supersedes the "an AI worker owns a static camera partition" design AiWorkerHealthRepository's
-- own doc comment describes (ai-worker/scaling.py's hash-based partitioning, computed client-side
-- and never reported back). That was fine while every worker saw the same fixed camera list and
-- WORKER_COUNT was set by hand; it has no answer for "a worker crashed, who picks up its
-- cameras?" and no visibility into which camera any given worker is actually watching. This adds
-- exactly what `federation.connector_target`'s own leased_by/lease_expires_at columns already do
-- for VMS connector workers (LeaseStore.cs), for AI workers and cameras instead:
--
--   - A worker claims up to its own capacity via POST /worker-health/cameras/claim, renewed on
--     every poll (same call). A camera whose lease goes stale (no renewal within the TTL) is
--     claimable by any other worker on its next poll — emergent rebalancing, no coordinator.
--   - Unlike connector_target (lease columns live directly on the table being leased),
--     "the camera" here is one of TWO different source tables — the registry (`cameras`) or a
--     VMS-discovered-but-not-yet-reconciled row (`federated_camera`, camera_id IS NULL) — so the
--     lease lives in its own table instead, keyed on a `camera_ref` that names either kind:
--     the registry UUID as text, or `vms:{targetId}:{nativeCameraId}` for the latter.
--
-- Deliberately NOT a schema change to GET /api/v1/cameras: that endpoint's contract (a real
-- registry UUID as `id`, every field non-null) is depended on by the whole frontend registry UI,
-- and a VMS-discovered-but-unreconciled camera has no registry UUID to give it. The claim
-- endpoint's own repository query unions both source tables internally instead — see
-- CameraLeaseRepository.
--
-- Also grants DETECTION_WORKER `camera.read`: discovering standalone (non-VMS) cameras needs it,
-- and this role never held it before now (v1.4 gave it vms.read/observation.write/worker.heartbeat
-- only — registry cameras weren't part of its job yet).
--
-- Requires: v1.sql .. v1.26.sql
-- ===========================================================================

SET search_path = federation, public;

CREATE TABLE IF NOT EXISTS camera_worker_lease (
    -- The registry camera's own UUID (as text) for a REGISTRY row, or
    -- 'vms:{targetId}:{nativeCameraId}' for a VMS row — see the module comment above. Primary key
    -- rather than a surrogate id: "one active lease per camera" is exactly what a claim needs to
    -- enforce, and an INSERT ... ON CONFLICT (camera_ref) upsert is the whole claim mechanism.
    camera_ref             TEXT PRIMARY KEY,
    source                 TEXT NOT NULL CHECK (source IN ('REGISTRY', 'VMS')),

    -- Denormalised from whichever source table camera_ref names, the same convention
    -- detection_event/detection_tag already use — a claim query scopes against this directly,
    -- never joining back to cameras/federated_camera to re-derive ownership.
    organization_unit_id   UUID NOT NULL REFERENCES organization_units(id),
    geographic_area_id     UUID,

    -- Matches ai_worker_health's own identity columns exactly (api_key_id, worker_id, hostname) —
    -- the same worker, identified the same way, now also owns a set of camera leases in addition
    -- to reporting heartbeats.
    worker_id               VARCHAR(128) NOT NULL,
    api_key_id               UUID NOT NULL REFERENCES api_key(id) ON DELETE CASCADE,
    hostname                 VARCHAR(253) NOT NULL,

    leased_at                TIMESTAMPTZ NOT NULL DEFAULT now(),
    lease_expires_at         TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_camera_worker_lease_owner
    ON camera_worker_lease (api_key_id, worker_id, hostname);
-- Powers "release everything past its TTL" scans and the admin view's staleness check.
CREATE INDEX IF NOT EXISTS ix_camera_worker_lease_expiry
    ON camera_worker_lease (lease_expires_at);

COMMENT ON TABLE camera_worker_lease IS
    'Dynamic camera<->AI-worker assignment (v1.27), superseding the static hash-partition '
    'ai-worker/scaling.py used to compute entirely client-side. One row per currently-leased '
    'camera; a stale lease (lease_expires_at < now()) is claimable by any worker on its next '
    'poll. camera_ref is either a registry camera UUID (source=REGISTRY) or '
    'vms:{targetId}:{nativeCameraId} for a VMS-discovered-but-unreconciled camera (source=VMS).';

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('DETECTION_WORKER', 'camera.read')
)
ON CONFLICT DO NOTHING;
