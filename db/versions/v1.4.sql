-- ===========================================================================
-- v1.4 — Detection-worker role, and the SUPER_ADMIN backfill it exposed
-- ===========================================================================
--
-- Model 2's AI worker (ai-worker/, Python) needs an API key that can:
--
--   * discover cameras            GET  /api/v1/vms, /vms/{id}/cameras   -> vms.read
--   * submit detections           POST /api/v1/detections               -> observation.write
--   * report its own liveness     POST /api/v1/worker-health/heartbeat  -> worker.heartbeat
--
-- A key acts through an access group, and a group pairs ONE role with scopes —
-- there is no way to attach a loose set of permissions to a group. So the
-- worker needs a role that holds exactly those three. None of the seeded roles
-- do, hence DETECTION_WORKER below.
--
-- WHY THE SUPER_ADMIN BACKFILL
--
-- v1.sql grants SUPER_ADMIN every permission with a CROSS JOIN, but that ran
-- before v1.2 added observation.write / watchlist.manage / worker.heartbeat and
-- before v1.3 added apikey.*. Those permissions are currently held by NO role,
-- so nobody — not even a super administrator — can reach POST /detections or
-- the worker-health endpoints. Re-running the CROSS JOIN fixes that generally;
-- it is idempotent.
--
-- Requires: v1.sql, v1.1.sql, v1.2.sql, v1.3.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- SUPER_ADMIN backfill — every permission, including those added since v1.sql
-- ---------------------------------------------------------------------------

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- The human side of the detection demo path (search / watchlist / worker view)
-- ---------------------------------------------------------------------------

-- STATE_ADMIN already administers everything else; it needs the read and
-- watchlist-management side so an operator can run the "search metadata ->
-- correlate -> raise alert" steps. Detection INGEST stays out — that is a
-- machine action, and DETECTION_WORKER is the only role that carries it.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN', 'observation.read'),
    ('STATE_ADMIN', 'watchlist.manage'),
    ('STATE_ADMIN', 'alert.read'),
    ('STATE_ADMIN', 'worker.heartbeat')
)
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- DETECTION_WORKER — the machine role for ai-worker/
-- ---------------------------------------------------------------------------

-- is_system: shipped with the platform, not operator-defined, so the UI and the
-- escalation guards treat it like the other built-ins.
INSERT INTO roles (code, name, description, is_system) VALUES
    ('DETECTION_WORKER', 'Detection Worker',
     'Model 2 AI worker: camera discovery, detection ingest and heartbeat. Machine only.', TRUE)
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, description = EXCLUDED.description;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('DETECTION_WORKER', 'vms.read'),
    ('DETECTION_WORKER', 'observation.write'),
    ('DETECTION_WORKER', 'worker.heartbeat')
)
ON CONFLICT DO NOTHING;
