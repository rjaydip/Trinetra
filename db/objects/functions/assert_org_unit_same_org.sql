-- Same-organization-as-parent invariant for organization_units (v1.12).
--
-- A DEFERRABLE constraint trigger, not a plain BEFORE trigger: a cross-organization
-- subtree move (OrganizationRepository.MoveUnitToOrganizationAsync) rewrites
-- organization_id across every descendant in one UPDATE and sets this constraint
-- DEFERRED, so it re-checks the whole subtree once, at commit, against the final
-- consistent state. INITIALLY IMMEDIATE otherwise: a direct malformed single-row
-- UPDATE (organization_id changed, parent left in the old organization) fails at once.

CREATE OR REPLACE FUNCTION federation.assert_org_unit_same_org() RETURNS trigger
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
BEGIN
    IF NEW.parent_unit_id IS NULL THEN
        RETURN NEW;
    END IF;

    IF (SELECT organization_id FROM organization_units WHERE id = NEW.parent_unit_id)
       IS DISTINCT FROM NEW.organization_id THEN
        RAISE EXCEPTION
            'Organization unit % must belong to the same organization as its parent.', NEW.id;
    END IF;

    RETURN NEW;
END $function$;

DROP TRIGGER IF EXISTS trg_org_unit_same_org ON federation.organization_units;
CREATE CONSTRAINT TRIGGER trg_org_unit_same_org
    AFTER INSERT OR UPDATE ON federation.organization_units
    DEFERRABLE INITIALLY IMMEDIATE
    FOR EACH ROW EXECUTE FUNCTION federation.assert_org_unit_same_org();
