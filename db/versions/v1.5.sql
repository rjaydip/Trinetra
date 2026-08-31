-- ===========================================================================
-- v1.5 — Stream-credential resolution for the AI worker
-- ===========================================================================
--
-- Model 2's AI worker (ai-worker/, Python) connects to camera RTSP streams
-- directly. Those streams are usually authenticated, and the worker had no way
-- to obtain the username/password: the registry never returns credential
-- values, and until now no route on the API did either.
--
-- This version adds ONE deliberately narrow read path:
--
--   GET /api/v1/vms/{id}/credential/resolve  -> credential.resolve
--
-- returning the resolved username/password/token for a connector target the
-- caller can already reach. It is the machine analogue of what the .NET
-- connector runtime already does in-process through ICredentialResolver, and
-- every resolution is written to credential_access_log exactly as that path is.
--
-- WHY THIS IS SAFE TO ADD
--
--   * credential.resolve is a distinct permission, held by exactly one role
--     (DETECTION_WORKER) — a machine role with no interactive login.
--   * Scope still runs through the target: a caller can only resolve a
--     credential for a VMS row their organization/geography grants reach to.
--   * The resolver audits every call, success or failure, so "which worker
--     read which credential, and when" stays answerable.
--   * PUT /vms/{id}/credential and /status are unchanged; the write side still
--     never echoes secret material.
--
-- Requires: v1.sql, v1.1.sql, v1.2.sql, v1.3.sql, v1.4.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Permission
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('credential.resolve', 'Resolve device credentials',
     'admin',
     'Read the resolved username/password/token for a connector target, in order to connect '
     || 'to it. Machine callers only; every resolution is audit-logged.')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN receives every permission (v1.sql's CROSS JOIN only ran against
-- the permissions that existed then). Re-run for the one just added.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- DETECTION_WORKER gains credential.resolve
-- ---------------------------------------------------------------------------

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('DETECTION_WORKER', 'credential.resolve')
)
ON CONFLICT DO NOTHING;
