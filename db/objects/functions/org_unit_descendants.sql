CREATE OR REPLACE FUNCTION federation.org_unit_descendants(root uuid) RETURNS TABLE(id uuid)
 LANGUAGE sql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
    WITH RECURSIVE tree(id) AS (
        SELECT root
        UNION ALL
        SELECT ou.id FROM organization_units ou JOIN tree t ON ou.parent_unit_id = t.id
    )
    SELECT id FROM tree;
$function$

