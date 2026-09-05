-- ===========================================================================
-- v1.7 — vms.delete permission
-- ===========================================================================
--
-- DELETE /api/v1/vms/{id} was gated on vms.update: the same permission that
-- lets someone edit a poll interval also let them permanently destroy a
-- target and its whole discovered inventory, capability matrix and health
-- history. That is the same smell camera.delete (v1.6) fixed for the camera
-- registry, applied here to the VMS registry. See docs/API-REVIEW-FINDINGS.md
-- Finding 9-H3.
--
-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

INSERT INTO permissions (code, name, category, description) VALUES
    ('vms.delete', 'Delete VMS integrations', 'vms',
     'Permanently remove a connector target and its discovered inventory')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN gets every permission, including this one. The CROSS JOIN only
-- runs when its own file runs, so every version that adds a permission must
-- repeat it (v1.4, v1.5, v1.6 all do).
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r CROSS JOIN permissions p WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- The same roles that hold vms.update today get vms.delete: deleting a target
-- is a natural extension of the same administrative authority that edits or
-- quarantines it, not a separately delegated action. Unlike camera.delete,
-- where VMS_ADMIN was deliberately excluded (it is the reconciliation user,
-- not the registry admin), VMS_ADMIN here IS the target's own admin — "the
-- Model 3 administrator" per its v1.sql description — so it is included.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN', 'vms.delete'),
    ('DEPARTMENT_ADMIN', 'vms.delete'),
    ('VMS_ADMIN', 'vms.delete')
)
ON CONFLICT DO NOTHING;
