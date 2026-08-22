CREATE OR REPLACE FUNCTION federation.geographic_area_ancestors(leaf uuid) RETURNS TABLE(id uuid)
 LANGUAGE sql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
    WITH RECURSIVE chain(id, parent_area_id) AS (
        SELECT ga.id, ga.parent_area_id FROM geographic_areas ga WHERE ga.id = leaf
        UNION ALL
        SELECT ga.id, ga.parent_area_id
          FROM geographic_areas ga JOIN chain c ON ga.id = c.parent_area_id
    )
    SELECT id FROM chain;
$function$

