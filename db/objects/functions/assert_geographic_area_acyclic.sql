CREATE OR REPLACE FUNCTION federation.assert_geographic_area_acyclic() RETURNS trigger
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    parent_level INTEGER;
    child_level  INTEGER;
BEGIN
    IF NEW.parent_area_id IS NOT NULL THEN
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

        SELECT t.level_order INTO parent_level
        FROM geographic_areas p
        JOIN geographic_area_types t ON t.code = p.area_type
        WHERE p.id = NEW.parent_area_id;

        SELECT t.level_order INTO child_level
        FROM geographic_area_types t WHERE t.code = NEW.area_type;

        IF parent_level IS NOT NULL AND child_level IS NOT NULL
           AND child_level <= parent_level THEN
            RAISE EXCEPTION
                'Geographic area % (type %) must sit at a finer level than its parent.',
                NEW.id, NEW.area_type;
        END IF;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM geographic_areas c
        JOIN geographic_area_types ct ON ct.code = c.area_type
        JOIN geographic_area_types nt ON nt.code = NEW.area_type
        WHERE c.parent_area_id = NEW.id
          AND ct.level_order <= nt.level_order
    ) THEN
        RAISE EXCEPTION
            'Geographic area % cannot be type % — a direct child is not finer than that.',
            NEW.id, NEW.area_type;
    END IF;

    RETURN NEW;
END $function$
