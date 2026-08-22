CREATE OR REPLACE FUNCTION federation.has_unscoped_permission(p_user_id uuid, p_api_key_id uuid, p_permission character varying) RETURNS boolean
 LANGUAGE sql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
    SELECT EXISTS (
        SELECT 1 FROM unscoped_permissions(p_user_id, p_api_key_id)
        WHERE permission_code = p_permission);
$function$

