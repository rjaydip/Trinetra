CREATE OR REPLACE FUNCTION federation.authorized_geographic_areas(p_user_id uuid, p_api_key_id uuid, p_permission character varying) RETURNS TABLE(geographic_area_id uuid)
 LANGUAGE sql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
    SELECT DISTINCT d.id
    FROM principal_groups(p_user_id, p_api_key_id) pg
    JOIN access_groups ag    ON ag.id = pg.group_id
    JOIN roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
    JOIN role_permissions rp ON rp.role_id = r.id AND rp.permission_code = p_permission
    JOIN group_scopes gs     ON gs.group_id = ag.id
    JOIN scopes s            ON s.id = gs.scope_id AND s.scope_type = 'GEOGRAPHY'
    CROSS JOIN LATERAL geographic_area_descendants(s.geographic_area_id) d;
$function$

