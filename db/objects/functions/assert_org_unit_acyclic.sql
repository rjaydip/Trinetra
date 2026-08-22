CREATE OR REPLACE FUNCTION federation.assert_org_unit_acyclic() RETURNS trigger
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
BEGIN
    IF NEW.parent_unit_id IS NULL THEN
        RETURN NEW;
    END IF;

    IF NEW.parent_unit_id = NEW.id THEN
        RAISE EXCEPTION 'Organization unit % cannot be its own parent.', NEW.id;
    END IF;

    -- A child must sit in the same organization as its parent, per DEPARTMENT-SCHEMA.md.
    IF (SELECT organization_id FROM organization_units WHERE id = NEW.parent_unit_id)
       IS DISTINCT FROM NEW.organization_id THEN
        RAISE EXCEPTION
            'Organization unit % must belong to the same organization as its parent.', NEW.id;
    END IF;

    IF EXISTS (
        WITH RECURSIVE ancestors(id) AS (
            SELECT parent_unit_id FROM organization_units
             WHERE id = NEW.parent_unit_id AND parent_unit_id IS NOT NULL
            UNION ALL
            SELECT ou.parent_unit_id FROM organization_units ou
              JOIN ancestors a ON ou.id = a.id
             WHERE ou.parent_unit_id IS NOT NULL
        )
        SELECT 1 FROM ancestors WHERE id = NEW.id
    ) THEN
        RAISE EXCEPTION 'Organization unit % would create a circular parent relationship.', NEW.id;
    END IF;

    RETURN NEW;
END $function$

