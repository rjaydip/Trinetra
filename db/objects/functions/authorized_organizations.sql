CREATE OR REPLACE FUNCTION federation.authorized_organizations(p_user_id uuid, p_api_key_id uuid, p_permission character varying) RETURNS TABLE(organization_id uuid)
 LANGUAGE sql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
    SELECT DISTINCT ou.organization_id
    FROM authorized_org_units(p_user_id, p_api_key_id, p_permission) a
    JOIN organization_units ou ON ou.id = a.organization_unit_id;
$function$

