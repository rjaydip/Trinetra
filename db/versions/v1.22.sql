-- ===========================================================================
-- v1.22 — camera credential resolution for the streaming gateway (G1)
-- ===========================================================================
--
-- docs/STREAMING-GATEWAY-PLAN.md's G1: the live-streaming gateway (browser-facing, opens RTSP
-- connections to cameras directly, same reason ai-worker needed v1.5's
-- GET /api/v1/vms/{id}/credential/resolve) needs the symmetric route for a STANDALONE registry
-- camera's own credential_reference:
--
--   GET /api/v1/cameras/{id}/credential/resolve  -> camera.credential.resolve
--
-- WHY A NEW PERMISSION AND A NEW ROLE, NOT REUSE OF v1.5's DETECTION_WORKER
--
--   * DETECTION_WORKER holds credential.resolve (VMS-target credentials) AND observation.write.
--     The streaming gateway is browser/operator-facing — a materially larger attack surface than
--     ai-worker's backend-only VMS polling — so a compromise there must not also be able to
--     forge ANPR/person/vehicle observations into the metadata layer that correlation, search
--     and alerting all trust. Two roles can each independently hold the SAME permission code
--     without coupling their other grants — STREAMING_GATEWAY holding credential.resolve does
--     not give it DETECTION_WORKER's observation.write, since permissions are independent grants
--     on a role, not a shared role. A distinct role is what actually keeps them apart; a distinct
--     permission code is not required for that, and camera.credential.resolve (standalone-camera
--     credentials) is still its own new permission because it gates a genuinely new route, not
--     because credential.resolve needed to be avoided.
--   * STREAMING_GATEWAY holds: camera.read (CameraRepository.GetAsync's own scope requirement),
--     camera.credential.resolve (the new route above, for a standalone camera's own
--     credential_reference), vms.read (ConnectorTargetRepository.GetAsync's equivalent
--     requirement), and credential.resolve (the EXISTING GET /vms/{id}/credential/resolve's
--     explicit .RequirePermission gate — vms.read alone satisfies only the repository's internal
--     scope check, not the route itself, which requires credential.resolve independently). The
--     vms.read + credential.resolve pair lets the gateway resolve a VMS-managed camera's
--     credential from its connector target when the camera's own credential_reference is null
--     (the ordinary case for such a camera — Federation.Core's Camera model never falls back
--     automatically; the caller resolves whichever row actually holds the reference). Still no
--     observation.write, no worker.heartbeat, nothing else DETECTION_WORKER has.
--
-- Requires: v1.sql .. v1.21.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Permission
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('camera.credential.resolve', 'Resolve standalone camera credentials',
     'admin',
     'Read the resolved username/password/token stored directly on a standalone registry '
     || 'camera, in order to connect to its stream. Machine callers only; every resolution is '
     || 'audit-logged. Distinct from credential.resolve (VMS-target credentials) so the two '
     || 'disclosure paths can be held by different, narrower machine roles.')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN receives every permission (v1.sql's CROSS JOIN only ran against the permissions
-- that existed then). Re-run for the one just added.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- STREAMING_GATEWAY — the machine role for the live-streaming gateway
-- ---------------------------------------------------------------------------

-- is_system: shipped with the platform, not operator-defined, so the UI and the escalation
-- guards treat it like the other built-ins. status is set ACTIVE explicitly: the column default
-- became DRAFT in v1.12 (which grants nothing), and presets are seeded ACTIVE.
INSERT INTO roles (code, name, description, is_system, status) VALUES
    ('STREAMING_GATEWAY', 'Streaming Gateway',
     'Live-streaming gateway (docs/STREAMING-GATEWAY-PLAN.md): resolves one camera''s credential '
     || 'to open its RTSP stream. Deliberately narrower than DETECTION_WORKER — no '
     || 'observation.write, no worker.heartbeat. Machine only.',
     TRUE, 'ACTIVE')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, description = EXCLUDED.description
    WHERE roles.customized_at IS NULL;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STREAMING_GATEWAY', 'camera.read'),
    ('STREAMING_GATEWAY', 'vms.read'),
    ('STREAMING_GATEWAY', 'camera.credential.resolve'),
    ('STREAMING_GATEWAY', 'credential.resolve')
)
ON CONFLICT DO NOTHING;
