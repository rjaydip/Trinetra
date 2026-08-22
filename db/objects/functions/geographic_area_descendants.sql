CREATE OR REPLACE FUNCTION federation.geographic_area_descendants(root uuid) RETURNS TABLE(id uuid)
 LANGUAGE sql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
    WITH RECURSIVE tree(id) AS (
        SELECT root
        UNION ALL
        SELECT ga.id FROM geographic_areas ga JOIN tree t ON ga.parent_area_id = t.id
    )
    SELECT id FROM tree;
$function$

