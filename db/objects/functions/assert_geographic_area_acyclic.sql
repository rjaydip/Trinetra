CREATE OR REPLACE FUNCTION federation.assert_geographic_area_acyclic() RETURNS trigger
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
BEGIN
    IF NEW.parent_area_id IS NULL THEN
        RETURN NEW;
    END IF;

    IF NEW.parent_area_id = NEW.id THEN
        RAISE EXCEPTION 'Geographic area % cannot be its own parent.', NEW.id;
    END IF;

    IF EXISTS (
        WITH RECURSIVE ancestors(id) AS (
            SELECT parent_area_id FROM geographic_areas
             WHERE id = NEW.parent_area_id AND parent_area_id IS NOT NULL
            UNION ALL
            SELECT ga.parent_area_id FROM geographic_areas ga
              JOIN ancestors a ON ga.id = a.id
             WHERE ga.parent_area_id IS NOT NULL
        )
        SELECT 1 FROM ancestors WHERE id = NEW.id
    ) THEN
        RAISE EXCEPTION 'Geographic area % would create a circular parent relationship.', NEW.id;
    END IF;

    RETURN NEW;
END $function$

