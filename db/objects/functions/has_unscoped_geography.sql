CREATE OR REPLACE FUNCTION federation.has_unscoped_geography(p_user_id uuid, p_api_key_id uuid, p_permission character varying) RETURNS boolean
 LANGUAGE sql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
    SELECT EXISTS (
        SELECT 1
        FROM principal_groups(p_user_id, p_api_key_id) pg
        JOIN access_groups ag    ON ag.id = pg.group_id
        JOIN roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
        JOIN role_permissions rp ON rp.role_id = r.id AND rp.permission_code = p_permission
        WHERE NOT EXISTS (
            SELECT 1 FROM group_scopes gs
            JOIN scopes s ON s.id = gs.scope_id
            WHERE gs.group_id = ag.id AND s.scope_type = 'GEOGRAPHY')
    );
$function$

