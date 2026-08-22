CREATE OR REPLACE FUNCTION federation.has_permission(p_user_id uuid, p_api_key_id uuid, p_permission character varying, p_organization_unit_id uuid DEFAULT NULL::uuid, p_geographic_area_id uuid DEFAULT NULL::uuid, p_resource_type character varying DEFAULT NULL::character varying, p_resource_id uuid DEFAULT NULL::uuid) RETURNS boolean
 LANGUAGE plpgsql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    grp                 RECORD;
    constrains_org      BOOLEAN;
    constrains_geo      BOOLEAN;
    constrains_resource BOOLEAN;
    org_ok              BOOLEAN;
    geo_ok              BOOLEAN;
    resource_ok         BOOLEAN;
BEGIN
    FOR grp IN
        SELECT DISTINCT pg.group_id
        FROM principal_groups(p_user_id, p_api_key_id) pg
        JOIN access_groups ag    ON ag.id = pg.group_id
        JOIN roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
        JOIN role_permissions rp ON rp.role_id = r.id
        WHERE rp.permission_code = p_permission
    LOOP
        SELECT
            bool_or(s.scope_type = 'ORGANIZATION'),
            bool_or(s.scope_type = 'GEOGRAPHY'),
            bool_or(s.scope_type = 'RESOURCE')
        INTO constrains_org, constrains_geo, constrains_resource
        FROM group_scopes gs JOIN scopes s ON s.id = gs.scope_id
        WHERE gs.group_id = grp.group_id;

        constrains_org      := COALESCE(constrains_org, FALSE);
        constrains_geo      := COALESCE(constrains_geo, FALSE);
        constrains_resource := COALESCE(constrains_resource, FALSE);

        -- Organization. A constrained group facing a resource with no owner is denied: an
        -- unowned resource is a data defect, and defaulting to allow would leak it to everyone.
        IF NOT constrains_org THEN
            org_ok := TRUE;
        ELSIF p_organization_unit_id IS NULL THEN
            org_ok := FALSE;
        ELSE
            SELECT EXISTS (
                SELECT 1
                FROM group_scopes gs
                JOIN scopes s ON s.id = gs.scope_id
                WHERE gs.group_id = grp.group_id
                  AND s.scope_type = 'ORGANIZATION'
                  AND p_organization_unit_id IN (
                        SELECT id FROM org_unit_descendants(s.organization_unit_id))
            ) INTO org_ok;
        END IF;

        -- Geography. Same rule: constrained group, unplaced resource, denied.
        IF NOT constrains_geo THEN
            geo_ok := TRUE;
        ELSIF p_geographic_area_id IS NULL THEN
            geo_ok := FALSE;
        ELSE
            SELECT EXISTS (
                SELECT 1
                FROM group_scopes gs
                JOIN scopes s ON s.id = gs.scope_id
                WHERE gs.group_id = grp.group_id
                  AND s.scope_type = 'GEOGRAPHY'
                  AND p_geographic_area_id IN (
                        SELECT id FROM geographic_area_descendants(s.geographic_area_id))
            ) INTO geo_ok;
        END IF;

        -- Resource pinning, per RBAC section 17: a group may be restricted to VMS-001 alone.
        IF NOT constrains_resource THEN
            resource_ok := TRUE;
        ELSIF p_resource_id IS NULL THEN
            resource_ok := FALSE;
        ELSE
            SELECT EXISTS (
                SELECT 1
                FROM group_scopes gs
                JOIN scopes s ON s.id = gs.scope_id
                WHERE gs.group_id = grp.group_id
                  AND s.scope_type = 'RESOURCE'
                  AND s.resource_type = p_resource_type
                  AND s.resource_id = p_resource_id
            ) INTO resource_ok;
        END IF;

        IF org_ok AND geo_ok AND resource_ok THEN
            RETURN TRUE;
        END IF;
    END LOOP;

    RETURN FALSE;
END $function$

