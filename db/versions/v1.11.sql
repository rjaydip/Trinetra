-- ===========================================================================
-- v1.11 — hierarchy: descriptions, area-type registry, sites removed
-- ===========================================================================
--
-- Three shape changes to the organization / geography / camera hierarchy,
-- none of which alters an authorization predicate or a security-function
-- signature:
--
--   1. description TEXT NULL on organization_units, geographic_areas and
--      geographic_area_types (organizations already has one). Every hierarchy
--      node can now carry an operator note that the GET/PUT round-trips.
--
--   2. organization_units.geographic_area_id — ONE optional "home area" label.
--      DESCRIPTIVE ONLY: for display and reporting, never an authorization
--      input. It is not read by has_permission, authorized_org_units,
--      authorized_geographic_areas, has_unscoped_geography or any scope
--      predicate — organization and geography stay independent scope
--      dimensions (CLAUDE.md invariant 12). The repository validates the area
--      exists and is ACTIVE; it does NOT check the caller's geographic scope,
--      because requiring geo access to set a label would couple the
--      dimensions.
--
--   3. geographic_areas.area_type becomes a required FK into
--      geographic_area_types(code) — the registry that v1 shipped empty. A
--      baseline set of levels is seeded here; any free-text area_type already
--      in use is registered at the end of the sort order (999). The acyclic
--      trigger gains LEVEL-ORDER CONTAINMENT: a child area must sit at a
--      strictly finer level than its parent (larger level_order). Same-level
--      and coarser children are rejected; skipping intervening levels is fine.
--      geographic_areas.code becomes unique PER PARENT rather than globally.
--
--   4. SITES ARE REMOVED. Cameras, targets and events attached to a physical
--      "site" that sat below the deepest geographic area. The owner's model is
--      a fully operator-defined geographic hierarchy with no fixed bottom
--      tier, so a camera now attaches directly to a geographic_area_id at any
--      level. Every site_id column is backfilled from the site's area and
--      replaced; the sites table is dropped. A camera keeps its own precise
--      latitude/longitude, so nothing about the map is lost.
--
--   5. Roles become editable PRESETS. New permission role.manage; the seeded
--      roles can be renamed and re-composed through the API (SUPER_ADMIN
--      excepted). roles.customized_at marks an operator-edited preset so a
--      later migration's re-seed does not revert it.
--
-- One new permission: role.manage (section 5). geography.manage /
-- organization.manage already gate the hierarchy writes; geography.read /
-- organization.read the reads.
--
-- MIGRATION SAFETY
--   THIS MIGRATION IS FRESH-INSTALL-ONLY as written. MigrationRunner wraps the
--   file in one transaction, so the CREATE INDEX statements below take ACCESS
--   EXCLUSIVE locks (no CONCURRENTLY inside a txn) and the site_id backfill
--   rewrites every row of the partitioned event tables. Against an 80k-camera
--   estate that is an outage. A populated-store rollout must be rewritten as a
--   multi-transaction runbook (indexes CONCURRENTLY, backfill batched per
--   partition off-peak). There is no production data today, so the single-txn
--   form is fine.
--   * Every ADD COLUMN is nullable at first. cameras.geographic_area_id is set
--     NOT NULL only after the backfill — safe because sites.geographic_area_id
--     was NOT NULL and cameras.site_id was NOT NULL, so the backfill is total.
--     Before SET NOT NULL runs, verify:
--         SELECT count(*) FROM cameras c
--         LEFT JOIN sites s ON s.id = c.site_id WHERE s.id IS NULL;   -- must be 0
--   * federated_camera.site_id was NOT NULL (v1.6); its replacement
--     geographic_area_id is left NULLABLE, matching connector_target — a target
--     with no area yields federated_camera rows with no area, and the scope
--     predicates already read a NULL area as "geo dimension does not constrain".
--   * The area_type backfill runs before the FK so the FK validates.
--   * DROP TABLE sites comes last, after every referencing column is gone.
--
-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;


-- ---------------------------------------------------------------------------
-- 1. description columns
-- ---------------------------------------------------------------------------

ALTER TABLE organization_units    ADD COLUMN IF NOT EXISTS description TEXT;
ALTER TABLE geographic_areas      ADD COLUMN IF NOT EXISTS description TEXT;
ALTER TABLE geographic_area_types ADD COLUMN IF NOT EXISTS description TEXT;


-- ---------------------------------------------------------------------------
-- 2. organization_units.geographic_area_id  (DESCRIPTIVE ONLY — see header)
-- ---------------------------------------------------------------------------

ALTER TABLE organization_units
    ADD COLUMN IF NOT EXISTS geographic_area_id UUID REFERENCES geographic_areas(id);

CREATE INDEX IF NOT EXISTS ix_org_unit_geo_area
    ON organization_units (geographic_area_id)
    WHERE geographic_area_id IS NOT NULL;

COMMENT ON COLUMN organization_units.geographic_area_id IS
    'DESCRIPTIVE ONLY. A single "home area" label for humans and the UI. Never '
    'an authorization input: not read by has_permission, authorized_org_units, '
    'authorized_geographic_areas, has_unscoped_geography or any scope predicate. '
    'Organization and geography are independent scope dimensions (invariant 12).';


-- ---------------------------------------------------------------------------
-- 3. geographic_area_types baseline + area_type FK + level-order containment
--    + per-parent code uniqueness
-- ---------------------------------------------------------------------------
--
-- geographic_area_types is a REGISTRY of level names, each with a level_order:
-- SMALLER = broader / higher in the tree (STATE 10), LARGER = finer / deeper
-- (BLOCK 70). It drives an ordered picker in the UI AND a containment rule:
-- a child area must sit at a STRICTLY FINER level than its parent
-- (child.level_order > parent.level_order). Same-level and coarser children are
-- rejected. Only the ORDERING is checked, never the size of the gap — an
-- operator may skip intervening levels (District straight to Village), and the
-- gaps of 10 in the baseline exist so custom tiers can be slotted in later.

-- Baseline levels, from docs/GEOGRAPHY-SCHEMA.md's two example families
-- (STATE->DISTRICT->TALUKA->VILLAGE and CITY->ZONE->WARD->SECTOR/BLOCK).
INSERT INTO geographic_area_types (code, name, level_order, description) VALUES
    ('STATE',    'State',    10, 'Top administrative region'),
    ('DIVISION', 'Division', 20, NULL),
    ('DISTRICT', 'District', 30, NULL),
    ('TALUKA',   'Taluka',   40, NULL),
    ('CITY',     'City',     40, NULL),
    ('ZONE',     'Zone',     50, NULL),
    ('WARD',     'Ward',     60, NULL),
    ('VILLAGE',  'Village',  60, NULL),
    ('SECTOR',   'Sector',   70, NULL),
    ('BLOCK',    'Block',    70, NULL)
ON CONFLICT (code) DO NOTHING;

-- Register any area_type already in use that the baseline doesn't cover, at the
-- end of the sort order. Such a level cannot nest under or over another baseline
-- level without tripping the containment check — the nudge to fix its order. No
-- production data today, so this is a safety net only.
INSERT INTO geographic_area_types (code, name, level_order)
SELECT DISTINCT ga.area_type, initcap(ga.area_type), 999
FROM   geographic_areas ga
WHERE  ga.area_type IS NOT NULL
  AND  length(trim(ga.area_type)) > 0
  AND  NOT EXISTS (SELECT 1 FROM geographic_area_types t WHERE t.code = ga.area_type)
ON CONFLICT (code) DO NOTHING;

ALTER TABLE geographic_areas
    ADD CONSTRAINT geographic_areas_area_type_fkey
    FOREIGN KEY (area_type) REFERENCES geographic_area_types(code);

-- Cycle prevention (pre-existing) PLUS level-order containment, in one trigger
-- function. Body-only change to the v1 function — same RETURNS TRIGGER
-- signature, so CREATE OR REPLACE is correct (invariant 13 is signature changes).
CREATE OR REPLACE FUNCTION assert_geographic_area_acyclic() RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
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

        -- Containment vs parent: child must be STRICTLY finer (larger level_order).
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

    -- Containment vs children: bites on an area_type change (a fresh INSERT has
    -- no children). A node may not be relabelled to a level equal to or finer
    -- than one of its own direct children.
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
END $$;

-- Fires on area_type changes too, so a level cannot be widened out from under
-- its children.
DROP TRIGGER IF EXISTS trg_geo_area_acyclic ON geographic_areas;
CREATE TRIGGER trg_geo_area_acyclic
    BEFORE INSERT OR UPDATE OF parent_area_id, area_type ON geographic_areas
    FOR EACH ROW EXECUTE FUNCTION assert_geographic_area_acyclic();

-- Area codes are unique WITHIN A PARENT, not globally. Sites are gone, so
-- operators build finer-grained trees and "WARD-1" recurs in every district.
-- NULLS NOT DISTINCT (PG15+) keeps roots unique on code among themselves.
ALTER TABLE geographic_areas DROP CONSTRAINT IF EXISTS geographic_areas_code_key;
ALTER TABLE geographic_areas
    ADD CONSTRAINT geographic_areas_parent_code_key
    UNIQUE NULLS NOT DISTINCT (parent_area_id, code);


-- ---------------------------------------------------------------------------
-- 4. Sites removed — every site_id becomes a direct geographic_area_id
-- ---------------------------------------------------------------------------

-- 4a. cameras: NOT NULL today, so backfill populates every row, then NOT NULL.
ALTER TABLE cameras
    ADD COLUMN IF NOT EXISTS geographic_area_id UUID REFERENCES geographic_areas(id);
UPDATE cameras c
   SET geographic_area_id = s.geographic_area_id
  FROM sites s WHERE s.id = c.site_id;
ALTER TABLE cameras ALTER COLUMN geographic_area_id SET NOT NULL;
CREATE INDEX IF NOT EXISTS ix_cameras_geo_area ON cameras (geographic_area_id);
ALTER TABLE cameras DROP COLUMN site_id;   -- cascades ix_cameras_site + the FK

-- 4b. connector_target: site_id nullable -> geographic_area_id nullable.
ALTER TABLE connector_target
    ADD COLUMN IF NOT EXISTS geographic_area_id UUID REFERENCES geographic_areas(id);
UPDATE connector_target t
   SET geographic_area_id = s.geographic_area_id
  FROM sites s WHERE s.id = t.site_id;
CREATE INDEX IF NOT EXISTS ix_target_geo_area ON connector_target (geographic_area_id);
ALTER TABLE connector_target DROP COLUMN site_id;

-- 4c. federated_camera.
ALTER TABLE federated_camera
    ADD COLUMN IF NOT EXISTS geographic_area_id UUID REFERENCES geographic_areas(id);
UPDATE federated_camera fc
   SET geographic_area_id = s.geographic_area_id
  FROM sites s WHERE s.id = fc.site_id;
CREATE INDEX IF NOT EXISTS ix_federated_camera_geo_area
    ON federated_camera (geographic_area_id);
ALTER TABLE federated_camera DROP COLUMN site_id;

-- 4d. detection_event (partitioned; drop the FK — a denormalised column, like
--     federation_event's, must not join back).
ALTER TABLE detection_event ADD COLUMN IF NOT EXISTS geographic_area_id UUID;
UPDATE detection_event d
   SET geographic_area_id = s.geographic_area_id
  FROM sites s WHERE s.id = d.site_id;
CREATE INDEX IF NOT EXISTS ix_detection_event_geo_area
    ON detection_event (geographic_area_id);
ALTER TABLE detection_event DROP COLUMN site_id;   -- cascades the sites FK

-- 4e. federation_event (partitioned, never had a site FK).
ALTER TABLE federation_event ADD COLUMN IF NOT EXISTS geographic_area_id UUID;
UPDATE federation_event e
   SET geographic_area_id = s.geographic_area_id
  FROM sites s WHERE s.id = e.site_id;
CREATE INDEX IF NOT EXISTS ix_federation_event_geo_area
    ON federation_event (geographic_area_id);
ALTER TABLE federation_event DROP COLUMN site_id;

-- 4f. camera_status_history (partitioned, no FK, trigger-populated).
ALTER TABLE camera_status_history ADD COLUMN IF NOT EXISTS geographic_area_id UUID;
UPDATE camera_status_history h
   SET geographic_area_id = s.geographic_area_id
  FROM sites s WHERE s.id = h.site_id;
ALTER TABLE camera_status_history DROP COLUMN site_id;

-- 4g. The trigger that feeds camera_status_history from federated_camera —
--     which now carries geographic_area_id instead of site_id.
CREATE OR REPLACE FUNCTION record_camera_status_change() RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
BEGIN
    INSERT INTO camera_status_history (
        target_id, native_camera_id, changed_at,
        organization_unit_id, geographic_area_id,
        previous_health, health,
        previous_enabled, is_enabled,
        previous_recording, is_recording)
    VALUES (
        NEW.target_id, NEW.native_camera_id, NEW.updated_at,
        NEW.organization_unit_id, NEW.geographic_area_id,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.health       END, NEW.health,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.is_enabled   END, NEW.is_enabled,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.is_recording END, NEW.is_recording)
    ON CONFLICT DO NOTHING;

    RETURN NULL;
END $$;

-- 4h. The table itself. Nothing references it now.
DROP TABLE sites;


-- ---------------------------------------------------------------------------
-- 5. Editable roles — role.manage permission + customization marker
-- ---------------------------------------------------------------------------
--
-- The seeded roles (SUPER_ADMIN, CAMERA_OPERATOR, VIEWER, ...) become PRESETS:
-- a starting point an operator can rename and re-compose through
-- POST/PUT /api/v1/roles, not a locked set. SUPER_ADMIN alone stays immutable
-- in the API — it is the recovery role the startup backfill and the
-- platform-admin group depend on. is_system now means only "shipped as a
-- preset": presets cannot be deleted or have their code changed; everything
-- else about them is editable.
--
--   role.manage — create and edit custom roles, and re-compose or disable the
--   preset roles. Because a role is a GLOBAL object with no scope of its own,
--   editing a preset changes every access group built on it in every
--   organization. So:
--     * Custom-role CRUD is available to any role.manage holder.
--     * Editing / disabling / deleting a PRESET (is_system) requires role.manage
--       held UNSCOPED — a scoped department admin must not be able to disable
--       VIEWER for the whole estate. Enforced in RoleEndpoints.
--   Every write also enforces that a caller not unscoped for role.manage cannot
--   put a permission into a role the caller does not already hold, otherwise
--   role.manage would be "grant yourself anything". SUPER_ADMIN receives
--   role.manage through the backfill below.

INSERT INTO permissions (code, name, category, description) VALUES
    ('role.manage', 'Manage roles', 'admin',
     'Create and edit custom roles; re-compose or disable preset roles (preset '
     || 'edits require the permission held unscoped)')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category,
        description = EXCLUDED.description;

-- SUPER_ADMIN gets every permission, including this one. The CROSS JOIN in
-- v1.sql only ran against the permissions that existed then.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r CROSS JOIN permissions p WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- Set by the API the first time an operator's edit actually changes a preset's
-- name, description or permission set (never on a no-op PUT). It exists so a
-- later migration does not silently revert a deliberate change:
--   * A migration that re-seeds preset name / description / permission sets MUST
--     scope its writes to `WHERE customized_at IS NULL`.
--   * A genuinely new permission a preset should carry is still added with an
--     explicit unconditional INSERT (like the SUPER_ADMIN backfill), with a
--     comment saying why it overrides customization.
-- SUPER_ADMIN is never customizable, so its backfill stays unconditional.
ALTER TABLE roles ADD COLUMN IF NOT EXISTS customized_at TIMESTAMPTZ;

COMMENT ON COLUMN roles.customized_at IS
    'Set by the API when an operator edit changes a preset (is_system) role. A '
    'migration that re-seeds preset name/description/permissions must skip rows '
    'where this is non-NULL; a deliberate new grant uses an explicit '
    'unconditional statement instead.';
