CREATE OR REPLACE FUNCTION federation.principal_permissions(p_user_id uuid, p_api_key_id uuid) RETURNS TABLE(permission_code character varying)
 LANGUAGE sql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
    SELECT DISTINCT rp.permission_code
    FROM principal_groups(p_user_id, p_api_key_id) pg
    JOIN access_groups ag    ON ag.id = pg.group_id
    JOIN roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
    JOIN role_permissions rp ON rp.role_id = r.id;
$function$

