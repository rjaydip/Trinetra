-- ===========================================================================
-- v1.12 — role & access-group status lifecycle; cross-organization unit move
-- ===========================================================================
--
-- Code-and-schema wave covering the hierarchy / RBAC lifecycle plan
-- (docs/API-PLAN-HIERARCHY-RBAC.md, docs/IMPL-NOTES-HIERARCHY-RBAC.md). Every
-- other item in that plan (P1, P2a, P3, P5, P6, P11, P12, P13) is code-only.
--
-- This file carries four schema changes, none of which alters an authorization
-- predicate or a security-function signature:
--
--   P9  — new permission role.read. Roles and permissions were read behind
--         group.read; they now have their own read permission, granted to
--         every role that currently holds group.read (unconditional backfill,
--         SUPER_ADMIN-style — a re-seed of preset composition must not drop it).
--
--   P7  — roles.status gains DRAFT. CHECK becomes ('DRAFT','ACTIVE','INACTIVE')
--         and the column default moves to 'DRAFT' (the API creates custom roles
--         as DRAFT). Existing rows stay as they are (all ACTIVE). No ARCHIVED
--         state: P8 soft-delete reuses INACTIVE. Every authz function already
--         joins roles r ... AND r.status = 'ACTIVE', so DRAFT / INACTIVE grant
--         nothing with no function change.
--
--   P10 — access_groups.status DISABLED -> INACTIVE, for consistency with
--         roles / organizations / areas / users. Rows migrated, then the CHECK
--         swapped. No authz function references the literal 'DISABLED'
--         (they only test = 'ACTIVE'). user_groups.status keeps its own
--         ('ACTIVE','REVOKED') domain — untouched.
--
--   P2b — cross-organization subtree move. assert_org_unit_acyclic() keeps
--         self-parent + cycle prevention but no longer cross-checks that a unit
--         sits in the same organization as its parent: a single
--         UPDATE ... WHERE id IN (org_unit_descendants(root)) rewrites a whole
--         subtree's organization_id and a per-row BEFORE trigger cannot see a
--         half-applied batch. The same-organization invariant is re-homed as a
--         DEFERRABLE constraint trigger (trg_org_unit_same_org) that
--         OrganizationRepository.MoveUnitToOrganizationAsync sets DEFERRED for
--         the move, so it re-checks the whole subtree once, at commit, against
--         the final consistent state. A direct malformed single-row UPDATE
--         (org changed, parent left behind) still fails immediately — the
--         constraint is INITIALLY IMMEDIATE.
--
-- CONSTRAINT-SWAP ORDERING: every CHECK change is DROP CONSTRAINT -> UPDATE
-- rows -> ADD CONSTRAINT, in that order. Get it wrong and the file fails to
-- apply, which breaks the whole integration suite (PostgresFixture.cs replays
-- every version file), not just one test.
--
-- Transaction is owned by the runner / test fixture: no BEGIN/COMMIT here.
-- v1.11 and earlier are never edited.
SET search_path TO federation, public;


-- ---------------------------------------------------------------------------
-- P9 — role.read permission
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('role.read', 'View roles', 'admin',
     'Read role definitions and the permission vocabulary')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category,
        description = EXCLUDED.description;

-- Grant role.read to every role that can already read access groups. Roles are
-- global reference data with no sensitive content, so this keeps existing
-- readers working. Unconditional and not scoped by customized_at (SUPER_ADMIN
-- backfill style): a later migration that re-seeds preset composition must not
-- silently strip it.
INSERT INTO role_permissions (role_id, permission_code)
SELECT DISTINCT rp.role_id, 'role.read'
FROM   role_permissions rp
WHERE  rp.permission_code = 'group.read'
ON CONFLICT DO NOTHING;

-- SUPER_ADMIN holds every permission; the v1.11 CROSS JOIN only covered the
-- permissions that existed then.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, 'role.read' FROM roles r WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;


-- ---------------------------------------------------------------------------
-- P7 — roles.status gains DRAFT; column default -> 'DRAFT'
-- ---------------------------------------------------------------------------

ALTER TABLE roles DROP CONSTRAINT IF EXISTS roles_status_check;
-- No row migration: every existing role is ACTIVE, which stays valid.
ALTER TABLE roles ADD CONSTRAINT roles_status_check
    CHECK ((status)::text = ANY (ARRAY['DRAFT', 'ACTIVE', 'INACTIVE']::text[]));

ALTER TABLE roles ALTER COLUMN status SET DEFAULT 'DRAFT';

COMMENT ON COLUMN roles.status IS
    'DRAFT (composed, not yet granting) -> ACTIVE -> INACTIVE. A custom role is '
    'created DRAFT by the API; presets are seeded ACTIVE. DELETE soft-deletes to '
    'INACTIVE. Every authz function requires status = ''ACTIVE'', so DRAFT and '
    'INACTIVE grant nothing.';


-- ---------------------------------------------------------------------------
-- P10 — access_groups.status  DISABLED -> INACTIVE
-- ---------------------------------------------------------------------------

ALTER TABLE access_groups DROP CONSTRAINT IF EXISTS access_groups_status_check;
UPDATE access_groups SET status = 'INACTIVE' WHERE status = 'DISABLED';
ALTER TABLE access_groups ADD CONSTRAINT access_groups_status_check
    CHECK ((status)::text = ANY (ARRAY['DRAFT', 'ACTIVE', 'INACTIVE']::text[]));

COMMENT ON COLUMN access_groups.status IS
    'DRAFT -> ACTIVE -> INACTIVE. An INACTIVE group grants nothing (authz '
    'functions only consider ACTIVE groups); membership rows survive so '
    're-activation restores the roster. Renamed from DISABLED in v1.12.';


-- ---------------------------------------------------------------------------
-- P2b — cross-organization unit move
-- ---------------------------------------------------------------------------
--
-- Body-only change to the v1 function: same RETURNS trigger signature, so
-- CREATE OR REPLACE is correct (invariant 13 is about signature changes). The
-- same-organization assertion moves out to a DEFERRABLE constraint trigger.

CREATE OR REPLACE FUNCTION assert_org_unit_acyclic() RETURNS trigger
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

    -- Same-organization-as-parent is enforced by trg_org_unit_same_org (a
    -- DEFERRABLE constraint trigger), not here. A cross-organization move
    -- (OrganizationRepository.MoveUnitToOrganizationAsync) rewrites organization_id
    -- across a whole subtree in one statement; a per-row BEFORE trigger fires in
    -- arbitrary order and would reject the legitimate half-applied batch. Cycle
    -- prevention stays here — the tree shape never changes mid-batch.
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
END $function$;

-- The same-organization invariant, deferrable so a subtree move can apply the
-- whole batch and have it re-checked once at commit against the final,
-- consistent state. INITIALLY IMMEDIATE: a direct malformed single-row UPDATE
-- (organization_id changed, parent left in the old org) still fails at once.
CREATE OR REPLACE FUNCTION assert_org_unit_same_org() RETURNS trigger
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

-- CREATE CONSTRAINT TRIGGER does not accept an UPDATE OF column list, so this fires on every
-- row update; the function no-ops immediately for a root (parent_unit_id IS NULL) and is one
-- indexed lookup otherwise.
DROP TRIGGER IF EXISTS trg_org_unit_same_org ON organization_units;
CREATE CONSTRAINT TRIGGER trg_org_unit_same_org
    AFTER INSERT OR UPDATE ON organization_units
    DEFERRABLE INITIALLY IMMEDIATE
    FOR EACH ROW EXECUTE FUNCTION assert_org_unit_same_org();
