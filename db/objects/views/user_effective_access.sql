CREATE OR REPLACE VIEW federation.user_effective_access AS
 SELECT u.id AS user_id,
    u.username,
    ag.id AS group_id,
    ag.code AS group_code,
    r.code AS role_code,
    rp.permission_code,
    s.scope_type,
    s.organization_unit_id,
    s.geographic_area_id,
    s.resource_type,
    s.resource_id
   FROM federation.platform_users u
     JOIN federation.user_groups ug ON ug.user_id = u.id AND ug.status::text = 'ACTIVE'::text AND (ug.expires_at IS NULL OR ug.expires_at > now())
     JOIN federation.access_groups ag ON ag.id = ug.group_id AND ag.status::text = 'ACTIVE'::text
     JOIN federation.roles r ON r.id = ag.role_id AND r.status::text = 'ACTIVE'::text
     JOIN federation.role_permissions rp ON rp.role_id = r.id
     LEFT JOIN federation.group_scopes gs ON gs.group_id = ag.id
     LEFT JOIN federation.scopes s ON s.id = gs.scope_id
  WHERE u.status::text = 'ACTIVE'::text;
