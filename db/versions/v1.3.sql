-- ===========================================================================
-- v1.3 — API key lifecycle
-- ===========================================================================
--
-- Adds the read/revoke side of the machine-to-machine API key feature. Create
-- already shipped (POST /api/v1/api-keys, on group.manage). This version:
--
--   * gives API keys their own permission pair rather than borrowing
--     group.manage, so key issuance can be delegated without also handing over
--     access-group editing;
--   * records WHO revoked a key, next to WHEN (revoked_at was already here,
--     unused).
--
-- The API moves all three api-key endpoints onto apikey.manage / apikey.read in
-- the same change, so create is never on a different permission from list and
-- revoke.
--
-- Requires: v1.sql, v1.1.sql, v1.2.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Permissions
-- ---------------------------------------------------------------------------

-- Issuing a credential another system authenticates with is a distinct, and
-- arguably higher, privilege than editing an access group's scopes. A separate
-- permission lets an operator delegate key rotation without conferring RBAC
-- editing, and keeps the audit query for "who touched API keys" exact.
INSERT INTO permissions (code, name, category, description) VALUES
    ('apikey.read',   'View API keys',   'admin', 'List machine-to-machine API keys and their lifecycle state'),
    ('apikey.manage', 'Manage API keys', 'admin', 'Provision and revoke machine-to-machine API keys')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN receives every permission, including any added by a later version
-- (v1.sql seeds this with a CROSS JOIN, which only ran against the permissions
-- that existed then). Re-run for the two just added.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN' AND p.code IN ('apikey.read', 'apikey.manage')
ON CONFLICT DO NOTHING;

-- STATE_ADMIN holds group.manage today and is the role the create endpoint was
-- reachable through, so it keeps the capability unbroken across this change.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN', 'apikey.read'),
    ('STATE_ADMIN', 'apikey.manage')
)
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- api_key.revoked_by
-- ---------------------------------------------------------------------------

-- config_audit already records the actor of a revoke, but a self-describing row
-- keeps the credential's own history readable without a join to the audit log,
-- and matches created_by, which is already here.
ALTER TABLE api_key
    ADD COLUMN IF NOT EXISTS revoked_by UUID REFERENCES platform_users(id);

COMMENT ON COLUMN api_key.revoked_by IS
    'The user who revoked this key. NULL while the key is live, or for a key revoked directly in SQL.';
