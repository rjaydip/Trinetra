-- ============================================================================
-- Trinetra — complete database (schema + reference data)
-- ============================================================================
--
-- Self-contained. Run against an EMPTY database and you have everything: tables,
-- types, functions, views, triggers, and the reference data (permissions, roles,
-- role_permissions) that authorisation resolves against.
--
--     psql -U trinetra -d trinetra -f db/full-schema.sql
--
-- This is db/versions/v1.sql .. v1.13.sql inlined in apply order. db/versions/*.sql
-- remains the source of truth (each file forward-only, never edited once applied —
-- see docs/OPERATIONS.md). Regenerate after adding a version file.
--
-- Do NOT run against a database that already has an earlier version; apply only the
-- individual version files it has not yet had.
--
-- It does NOT include db/seed/dev-sample-data.sql (development fixtures only).
--
-- Plain SQL — no psql meta-commands. Run it with psql, a GUI client, or straight from an
-- application (one multi-statement command). The whole file is one transaction: any error rolls
-- everything back, so a partial schema is never left behind.
-- ============================================================================

BEGIN;


-- ####################################################################
-- ##  versions/v1.sql
-- ####################################################################

-- Trinetra — schema v1
--
-- The complete database for version 1. Apply it to an empty database and you have everything:
-- tables, functions, views, and the reference data (permissions, roles, role_permissions) that
-- authorisation resolves against. A schema-only file would apply cleanly and then authorise
-- nobody, so the seed data is part of this file rather than a separate step.
--
--     psql -U trinetra -d trinetra -f db/versions/v1.sql
--
-- Later changes go in their own file -- v1.1.sql holds only what v1.1 adds, applied on top of a
-- v1 database. This file is never edited once it has been applied anywhere: installs that ran
-- the old text would silently differ from those that ran the new one.
--
-- No extensions are required. Coordinates are plain DECIMAL columns with range checks, and
-- gen_random_uuid() is core PostgreSQL from 13 onward.


-- ===========================================================================
-- organizations and geography
-- ===========================================================================
-- Organizational and geographic hierarchies.
--
-- Per DEPARTMENT-SCHEMA.md and GEOGRAPHY-SCHEMA.md these are two INDEPENDENT dimensions.
-- A camera has an owner (organization) and a place (geography); merging them into one tree
-- makes "which departments have cameras at this site?" unanswerable.
--
-- Both hierarchies are generic and self-referencing. The level names (commissionerate, zone,
-- district, taluka, ward) are operator data, not schema. A second deployment with a completely
-- different structure needs no migration.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
--
-- Plain SET, not SET LOCAL. SET LOCAL is silently a NO-OP outside a transaction block, so a
-- migration applied with plain psql would create every object in `public` instead of
-- `federation` and report success while doing it.
SET search_path TO federation, public;


CREATE SCHEMA IF NOT EXISTS federation;

-- ---------------------------------------------------------------------------
-- Organization hierarchy
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS organizations (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code                VARCHAR(50)  NOT NULL UNIQUE,
    name                VARCHAR(255) NOT NULL,
    organization_type   VARCHAR(50)  NOT NULL,
    description         TEXT,
    status              VARCHAR(20)  NOT NULL DEFAULT 'ACTIVE'
                        CHECK (status IN ('ACTIVE', 'INACTIVE')),
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS organization_units (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id     UUID NOT NULL REFERENCES organizations(id),
    parent_unit_id      UUID REFERENCES organization_units(id),
    code                VARCHAR(50)  NOT NULL UNIQUE,
    name                VARCHAR(255) NOT NULL,
    unit_type           VARCHAR(50)  NOT NULL,
    status              VARCHAR(20)  NOT NULL DEFAULT 'ACTIVE'
                        CHECK (status IN ('ACTIVE', 'INACTIVE')),
    metadata            JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_org_unit_parent ON organization_units (parent_unit_id);
CREATE INDEX IF NOT EXISTS ix_org_unit_org    ON organization_units (organization_id);

-- ---------------------------------------------------------------------------
-- Geographic hierarchy
-- ---------------------------------------------------------------------------

-- Optional level registry. Without it `area_type` is free text; with it the API can reject a
-- district placed inside a village, and the UI can render levels in a consistent order.
CREATE TABLE IF NOT EXISTS geographic_area_types (
    code            VARCHAR(50) PRIMARY KEY,
    name            VARCHAR(255) NOT NULL,
    level_order     INTEGER NOT NULL,
    status          VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
                    CHECK (status IN ('ACTIVE', 'INACTIVE'))
);

CREATE TABLE IF NOT EXISTS geographic_areas (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    parent_area_id      UUID REFERENCES geographic_areas(id),
    code                VARCHAR(50)  NOT NULL UNIQUE,
    name                VARCHAR(255) NOT NULL,
    -- Operator-defined: STATE/DISTRICT/TALUKA/VILLAGE, or CITY/ZONE/SECTOR/BLOCK. Not an enum,
    -- because constraining it here would force a schema change per deployment.
    area_type           VARCHAR(50)  NOT NULL,
    status              VARCHAR(20)  NOT NULL DEFAULT 'ACTIVE'
                        CHECK (status IN ('ACTIVE', 'INACTIVE')),
    metadata            JSONB NOT NULL DEFAULT '{}'::jsonb,
    -- Optional. Containment resolves from the hierarchy alone; a polygon only adds map
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_geo_area_parent ON geographic_areas (parent_area_id);
CREATE INDEX IF NOT EXISTS ix_geo_area_type   ON geographic_areas (area_type);

CREATE TABLE IF NOT EXISTS sites (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code                VARCHAR(100) NOT NULL UNIQUE,
    name                VARCHAR(255) NOT NULL,
    geographic_area_id  UUID NOT NULL REFERENCES geographic_areas(id),
    site_type           VARCHAR(50),
    address             TEXT,
    -- The site's own centre. A camera keeps its own precise coordinates, because several
    -- cameras at one junction sit on different poles facing different ways.
    latitude            DECIMAL(10, 7) CHECK (latitude  BETWEEN -90  AND 90),
    longitude           DECIMAL(10, 7) CHECK (longitude BETWEEN -180 AND 180),
    status              VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
                        CHECK (status IN ('ACTIVE', 'INACTIVE')),
    metadata            JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_site_area ON sites (geographic_area_id);

-- ---------------------------------------------------------------------------
-- Hierarchy integrity
-- ---------------------------------------------------------------------------
-- Cycles cannot be expressed as a CHECK constraint, and a cycle is not a cosmetic problem:
-- every scope-resolution query walks these trees with a recursive CTE, so one cycle hangs
-- authorization for every user whose scope touches that branch.

CREATE OR REPLACE FUNCTION assert_org_unit_acyclic() RETURNS TRIGGER
LANGUAGE plpgsql
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
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
END $$;

CREATE OR REPLACE FUNCTION assert_geographic_area_acyclic() RETURNS TRIGGER
LANGUAGE plpgsql
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
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
END $$;

DROP TRIGGER IF EXISTS trg_org_unit_acyclic ON organization_units;
CREATE TRIGGER trg_org_unit_acyclic
    BEFORE INSERT OR UPDATE OF parent_unit_id, organization_id ON organization_units
    FOR EACH ROW EXECUTE FUNCTION assert_org_unit_acyclic();

DROP TRIGGER IF EXISTS trg_geo_area_acyclic ON geographic_areas;
CREATE TRIGGER trg_geo_area_acyclic
    BEFORE INSERT OR UPDATE OF parent_area_id ON geographic_areas
    FOR EACH ROW EXECUTE FUNCTION assert_geographic_area_acyclic();

-- ---------------------------------------------------------------------------
-- Hierarchy resolution
-- ---------------------------------------------------------------------------
-- Scope matching asks "is this resource inside that scope?". Both answers walk a tree, so the
-- walk lives here once rather than being re-expressed in every query that needs it.
--
-- This is why scope rows name a single area or unit and never list descendants: adding a
-- village must not require finding and updating every scope that should now include it.

CREATE OR REPLACE FUNCTION org_unit_descendants(root UUID)
RETURNS TABLE (id UUID) LANGUAGE sql STABLE
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
    WITH RECURSIVE tree(id) AS (
        SELECT root
        UNION ALL
        SELECT ou.id FROM organization_units ou JOIN tree t ON ou.parent_unit_id = t.id
    )
    SELECT id FROM tree;
$$;

CREATE OR REPLACE FUNCTION geographic_area_descendants(root UUID)
RETURNS TABLE (id UUID) LANGUAGE sql STABLE
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
    WITH RECURSIVE tree(id) AS (
        SELECT root
        UNION ALL
        SELECT ga.id FROM geographic_areas ga JOIN tree t ON ga.parent_area_id = t.id
    )
    SELECT id FROM tree;
$$;

CREATE OR REPLACE FUNCTION geographic_area_ancestors(leaf UUID)
RETURNS TABLE (id UUID) LANGUAGE sql STABLE
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
    WITH RECURSIVE chain(id, parent_area_id) AS (
        SELECT ga.id, ga.parent_area_id FROM geographic_areas ga WHERE ga.id = leaf
        UNION ALL
        SELECT ga.id, ga.parent_area_id
          FROM geographic_areas ga JOIN chain c ON ga.id = c.parent_area_id
    )
    SELECT id FROM chain;
$$;


-- ===========================================================================
-- identity and rbac
-- ===========================================================================
-- Identity and group-based RBAC.
--
-- Implements RBAC-LOGICAL-FLOW.md directly:
--
--     User -> Access Group -> Role -> Permissions
--                          -> Scopes -> Organization / Geography / Resource
--
-- Role answers "what can they do"; Scope answers "where, or on which resources". Keeping them
-- separate is what avoids the role explosion in section 15 -- one CAMERA_OPERATOR role reused
-- across many groups, rather than AhmedabadPoliceCameraOperator, SuratPoliceCameraOperator and
-- so on forever.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

-- ---------------------------------------------------------------------------
-- Permissions and roles (reference data)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS permissions (
    code            VARCHAR(100) PRIMARY KEY,
    name            VARCHAR(255) NOT NULL,
    category        VARCHAR(50)  NOT NULL,
    description     TEXT
);

CREATE TABLE IF NOT EXISTS roles (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code            VARCHAR(50)  NOT NULL UNIQUE,
    name            VARCHAR(255) NOT NULL,
    description     TEXT,
    -- System roles ship with the platform and cannot be deleted. Removing SUPER_ADMIN while
    -- it is the only account able to create others would lock everyone out permanently.
    is_system       BOOLEAN NOT NULL DEFAULT FALSE,
    status          VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
                    CHECK (status IN ('ACTIVE', 'INACTIVE')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS role_permissions (
    role_id         UUID NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission_code VARCHAR(100) NOT NULL REFERENCES permissions(code),
    PRIMARY KEY (role_id, permission_code)
);

-- ---------------------------------------------------------------------------
-- Users
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS platform_users (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    username                VARCHAR(100) NOT NULL UNIQUE,
    display_name            VARCHAR(255) NOT NULL,
    email                   VARCHAR(255),

    -- PBKDF2-HMAC-SHA256. Parameters are stored per row so the cost can be raised over time
    -- without invalidating existing passwords: an old row verifies with its own iteration
    -- count and is rehashed at next successful login.
    password_hash           BYTEA NOT NULL,
    password_salt           BYTEA NOT NULL,
    password_iterations     INTEGER NOT NULL,
    password_algorithm      VARCHAR(50) NOT NULL DEFAULT 'PBKDF2-HMAC-SHA256',

    -- Set on the seeded administrator. The bootstrap password in configuration is not meant
    -- to survive first contact with a human.
    must_change_password    BOOLEAN NOT NULL DEFAULT FALSE,

    status                  VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
                            CHECK (status IN ('ACTIVE', 'INACTIVE', 'LOCKED')),

    failed_login_count      INTEGER NOT NULL DEFAULT 0,
    locked_until            TIMESTAMPTZ,
    last_login_at           TIMESTAMPTZ,

    -- Marks the seeded account so it can be recognised without matching on a username someone
    -- may later rename.
    is_system               BOOLEAN NOT NULL DEFAULT FALSE,

    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- Scopes
-- ---------------------------------------------------------------------------
-- A scope names exactly ONE point in ONE dimension. Everything beneath it is included by
-- hierarchy resolution (see org_unit_descendants / geographic_area_descendants).
--
-- Scopes must never enumerate descendants. If they did, adding a village would grant nobody
-- access until every affected scope row was found and updated -- and nothing would report the
-- ones that were missed.

CREATE TABLE IF NOT EXISTS scopes (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    scope_type              VARCHAR(20) NOT NULL
                            CHECK (scope_type IN ('ORGANIZATION', 'GEOGRAPHY', 'RESOURCE')),

    organization_unit_id    UUID REFERENCES organization_units(id),
    geographic_area_id      UUID REFERENCES geographic_areas(id),

    -- RESOURCE scope pins a specific VMS, adapter or camera, per RBAC section 17.
    resource_type           VARCHAR(50),
    resource_id             UUID,

    description             VARCHAR(255),
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Exactly one dimension is populated. A half-filled scope row would silently match
    -- everything or nothing depending on how a query was written.
    CONSTRAINT ck_scope_single_dimension CHECK (
        (scope_type = 'ORGANIZATION'
             AND organization_unit_id IS NOT NULL
             AND geographic_area_id IS NULL AND resource_id IS NULL)
     OR (scope_type = 'GEOGRAPHY'
             AND geographic_area_id IS NOT NULL
             AND organization_unit_id IS NULL AND resource_id IS NULL)
     OR (scope_type = 'RESOURCE'
             AND resource_type IS NOT NULL AND resource_id IS NOT NULL
             AND organization_unit_id IS NULL AND geographic_area_id IS NULL)
    )
);

-- ---------------------------------------------------------------------------
-- Access groups
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS access_groups (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code            VARCHAR(50)  NOT NULL UNIQUE,
    name            VARCHAR(255) NOT NULL,
    description     TEXT,
    role_id         UUID NOT NULL REFERENCES roles(id),
    -- DRAFT groups grant nothing, so a group can be assembled and reviewed before it is live.
    status          VARCHAR(20) NOT NULL DEFAULT 'DRAFT'
                    CHECK (status IN ('DRAFT', 'ACTIVE', 'DISABLED')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by      UUID REFERENCES platform_users(id),
    updated_by      UUID REFERENCES platform_users(id)
);

CREATE TABLE IF NOT EXISTS group_scopes (
    group_id        UUID NOT NULL REFERENCES access_groups(id) ON DELETE CASCADE,
    scope_id        UUID NOT NULL REFERENCES scopes(id) ON DELETE CASCADE,
    PRIMARY KEY (group_id, scope_id)
);

CREATE TABLE IF NOT EXISTS user_groups (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id         UUID NOT NULL REFERENCES platform_users(id) ON DELETE CASCADE,
    group_id        UUID NOT NULL REFERENCES access_groups(id) ON DELETE CASCADE,
    assigned_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    assigned_by     UUID REFERENCES platform_users(id),
    -- Temporary access, per RBAC section 26. An investigation team's membership lapses on its
    -- own rather than depending on somebody remembering to revoke it.
    expires_at      TIMESTAMPTZ,
    status          VARCHAR(20) NOT NULL DEFAULT 'ACTIVE'
                    CHECK (status IN ('ACTIVE', 'REVOKED')),
    UNIQUE (user_id, group_id)
);

CREATE INDEX IF NOT EXISTS ix_user_groups_user ON user_groups (user_id)
    WHERE status = 'ACTIVE';

-- ---------------------------------------------------------------------------
-- Effective permissions
-- ---------------------------------------------------------------------------
-- One place that answers "what can this user do, and where". Section 13: multiple groups union
-- their permissions, but each permission stays bound to ITS OWN group's scopes -- a user is not
-- granted Group A's permission within Group B's scope.

CREATE OR REPLACE VIEW user_effective_access AS
SELECT
    u.id                AS user_id,
    u.username,
    ag.id               AS group_id,
    ag.code             AS group_code,
    r.code              AS role_code,
    rp.permission_code,
    s.scope_type,
    s.organization_unit_id,
    s.geographic_area_id,
    s.resource_type,
    s.resource_id
FROM platform_users u
JOIN user_groups ug     ON ug.user_id = u.id
                        AND ug.status = 'ACTIVE'
                        AND (ug.expires_at IS NULL OR ug.expires_at > now())
JOIN access_groups ag   ON ag.id = ug.group_id AND ag.status = 'ACTIVE'
JOIN roles r            ON r.id = ag.role_id AND r.status = 'ACTIVE'
JOIN role_permissions rp ON rp.role_id = r.id
LEFT JOIN group_scopes gs ON gs.group_id = ag.id
LEFT JOIN scopes s        ON s.id = gs.scope_id
WHERE u.status = 'ACTIVE';


-- ===========================================================================
-- rbac seed data
-- ===========================================================================
-- Permission and role reference data.
--
-- The permission vocabulary is taken verbatim from RBAC-LOGICAL-FLOW.md section 3. Roles are
-- from the same section, with permission sets assigned here.
--
-- Seeded as data rather than hardcoded in application code so an operator can inspect exactly
-- what a role grants by querying the database, and so a deployment can add its own roles
-- without a code change.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

INSERT INTO permissions (code, name, category, description) VALUES
    -- Camera
    ('camera.read',                 'View cameras',              'camera',   'Read camera registry entries'),
    ('camera.create',               'Register cameras',          'camera',   'Add cameras to the registry'),
    ('camera.update',               'Edit cameras',              'camera',   'Modify camera registry entries'),
    ('camera.health.read',          'View camera health',        'camera',   'Read camera health status and history'),
    ('camera.maintenance.read',     'View maintenance',          'camera',   'Read maintenance records'),
    ('camera.maintenance.update',   'Record maintenance',        'camera',   'Create and update maintenance records'),

    -- Video
    ('video.read',                  'View video',                'video',    'Access live video references'),
    ('video.playback',              'Play recordings',           'video',    'Access recorded video'),

    -- Observations and events
    ('observation.read',            'View observations',         'analytics','Read Model 2 observations'),
    ('observation.search',          'Search observations',       'analytics','Query observations'),
    ('event.read',                  'View events',               'events',   'Read normalised events'),
    ('event.acknowledge',           'Acknowledge events',        'events',   'Mark events as acknowledged'),

    -- Alerts
    ('alert.read',                  'View alerts',               'alerts',   'Read alerts'),
    ('alert.acknowledge',           'Acknowledge alerts',        'alerts',   'Acknowledge an alert'),
    ('alert.resolve',               'Resolve alerts',            'alerts',   'Close an alert'),

    -- VMS and integration (Model 3)
    ('vms.read',                    'View VMS integrations',     'vms',      'Read connector targets'),
    ('vms.create',                  'Add VMS integrations',      'vms',      'Register a connector target'),
    ('vms.update',                  'Edit VMS integrations',     'vms',      'Modify or disable a connector target'),
    ('adapter.read',                'View adapters',             'vms',      'Read adapter capability matrices'),
    ('adapter.create',              'Add adapters',              'vms',      'Register an adapter configuration'),
    ('adapter.update',              'Edit adapters',             'vms',      'Modify an adapter configuration'),
    ('integration.manage',          'Manage integrations',       'vms',      'Full integration administration'),

    -- GIS
    ('gis.read',                    'View map',                  'gis',      'Read GIS layers'),
    ('gis.coverage.read',           'View coverage',             'gis',      'Read coverage and gap analysis'),

    -- Investigation
    ('vehicle_search.execute',      'Search vehicles',           'search',   'Run vehicle/ANPR searches'),
    ('person_search.execute',       'Search persons',            'search',   'Run person searches'),

    -- Audit
    ('audit.read',                  'View audit log',            'audit',    'Read audit records'),

    -- Administration.
    -- These extend RBAC-LOGICAL-FLOW.md section 3, which covers operational resources only.
    -- The API also has to administer the organization, geography and identity data that every
    -- other permission is scoped against, and those actions need permissions of their own.
    ('organization.read',           'View organizations',        'admin',    'Read organizations and units'),
    ('organization.manage',         'Manage organizations',      'admin',    'Create and edit organizations and units'),
    ('geography.read',              'View geography',            'admin',    'Read geographic areas and sites'),
    ('geography.manage',            'Manage geography',          'admin',    'Create and edit geographic areas and sites'),
    ('user.read',                   'View users',                'admin',    'Read platform users'),
    ('user.manage',                 'Manage users',              'admin',    'Create, edit and deactivate users'),
    ('group.read',                  'View access groups',        'admin',    'Read access groups and scopes'),
    ('group.manage',                'Manage access groups',      'admin',    'Create and edit access groups and scopes'),
    ('credential.write',            'Set credentials',           'admin',    'Write device credentials. Highest privilege in the system')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

INSERT INTO roles (code, name, description, is_system) VALUES
    ('SUPER_ADMIN',          'Super Administrator',   'Unrestricted platform administration',        TRUE),
    ('STATE_ADMIN',          'State Administrator',   'Administration across all organizations',     TRUE),
    ('DEPARTMENT_ADMIN',     'Department Administrator','Administration within their organization',  TRUE),
    ('VMS_ADMIN',            'VMS Administrator',     'Full VMS and adapter administration',         TRUE),
    ('VMS_OPERATOR',         'VMS Operator',          'Operate VMS integrations without configuring',TRUE),
    ('CAMERA_OPERATOR',      'Camera Operator',       'Day-to-day camera and video operation',       TRUE),
    ('INVESTIGATOR',         'Investigator',          'Search and investigation across evidence',    TRUE),
    ('MAINTENANCE_OPERATOR', 'Maintenance Operator',  'Camera health and maintenance',               TRUE),
    ('ANALYST',              'Analyst',               'Read-only analytics and reporting',           TRUE),
    ('VIEWER',               'Viewer',                'Read-only access',                            TRUE)
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, description = EXCLUDED.description;

-- SUPER_ADMIN receives every permission, including any added by a later migration. Enumerating
-- them would mean a new permission silently not reaching the only account able to grant it.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r CROSS JOIN permissions p WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    -- STATE_ADMIN: everything operational plus administration, but not credential writing.
    ('STATE_ADMIN','organization.read'), ('STATE_ADMIN','organization.manage'),
    ('STATE_ADMIN','geography.read'), ('STATE_ADMIN','geography.manage'),
    ('STATE_ADMIN','user.read'), ('STATE_ADMIN','user.manage'),
    ('STATE_ADMIN','group.read'), ('STATE_ADMIN','group.manage'),
    ('STATE_ADMIN','camera.read'), ('STATE_ADMIN','camera.create'), ('STATE_ADMIN','camera.update'),
    ('STATE_ADMIN','vms.read'), ('STATE_ADMIN','vms.create'), ('STATE_ADMIN','vms.update'),
    ('STATE_ADMIN','adapter.read'), ('STATE_ADMIN','event.read'), ('STATE_ADMIN','audit.read'),
    ('STATE_ADMIN','gis.read'), ('STATE_ADMIN','gis.coverage.read'),

    ('DEPARTMENT_ADMIN','organization.read'), ('DEPARTMENT_ADMIN','geography.read'),
    ('DEPARTMENT_ADMIN','user.read'), ('DEPARTMENT_ADMIN','group.read'),
    ('DEPARTMENT_ADMIN','camera.read'), ('DEPARTMENT_ADMIN','camera.create'),
    ('DEPARTMENT_ADMIN','camera.update'), ('DEPARTMENT_ADMIN','camera.health.read'),
    ('DEPARTMENT_ADMIN','vms.read'), ('DEPARTMENT_ADMIN','vms.update'),
    ('DEPARTMENT_ADMIN','adapter.read'), ('DEPARTMENT_ADMIN','event.read'),
    ('DEPARTMENT_ADMIN','alert.read'), ('DEPARTMENT_ADMIN','audit.read'),
    ('DEPARTMENT_ADMIN','gis.read'), ('DEPARTMENT_ADMIN','gis.coverage.read'),

    -- VMS_ADMIN: the Model 3 administrator. Credential writing is deliberately included here
    -- and nowhere else below -- it is the highest-privilege action in the system.
    ('VMS_ADMIN','vms.read'), ('VMS_ADMIN','vms.create'), ('VMS_ADMIN','vms.update'),
    ('VMS_ADMIN','adapter.read'), ('VMS_ADMIN','adapter.create'), ('VMS_ADMIN','adapter.update'),
    ('VMS_ADMIN','integration.manage'), ('VMS_ADMIN','credential.write'),
    ('VMS_ADMIN','event.read'), ('VMS_ADMIN','camera.read'), ('VMS_ADMIN','camera.health.read'),
    ('VMS_ADMIN','organization.read'), ('VMS_ADMIN','geography.read'), ('VMS_ADMIN','audit.read'),

    ('VMS_OPERATOR','vms.read'), ('VMS_OPERATOR','adapter.read'), ('VMS_OPERATOR','event.read'),
    ('VMS_OPERATOR','event.acknowledge'), ('VMS_OPERATOR','camera.read'),
    ('VMS_OPERATOR','camera.health.read'), ('VMS_OPERATOR','organization.read'),
    ('VMS_OPERATOR','geography.read'),

    ('CAMERA_OPERATOR','camera.read'), ('CAMERA_OPERATOR','camera.health.read'),
    ('CAMERA_OPERATOR','video.read'), ('CAMERA_OPERATOR','event.read'),
    ('CAMERA_OPERATOR','event.acknowledge'), ('CAMERA_OPERATOR','alert.read'),
    ('CAMERA_OPERATOR','alert.acknowledge'), ('CAMERA_OPERATOR','gis.read'),
    ('CAMERA_OPERATOR','organization.read'), ('CAMERA_OPERATOR','geography.read'),

    ('INVESTIGATOR','camera.read'), ('INVESTIGATOR','video.read'), ('INVESTIGATOR','video.playback'),
    ('INVESTIGATOR','observation.read'), ('INVESTIGATOR','observation.search'),
    ('INVESTIGATOR','event.read'), ('INVESTIGATOR','alert.read'),
    ('INVESTIGATOR','vehicle_search.execute'), ('INVESTIGATOR','person_search.execute'),
    ('INVESTIGATOR','gis.read'), ('INVESTIGATOR','organization.read'), ('INVESTIGATOR','geography.read'),

    ('MAINTENANCE_OPERATOR','camera.read'), ('MAINTENANCE_OPERATOR','camera.health.read'),
    ('MAINTENANCE_OPERATOR','camera.maintenance.read'), ('MAINTENANCE_OPERATOR','camera.maintenance.update'),
    ('MAINTENANCE_OPERATOR','vms.read'), ('MAINTENANCE_OPERATOR','gis.read'),
    ('MAINTENANCE_OPERATOR','organization.read'), ('MAINTENANCE_OPERATOR','geography.read'),

    ('ANALYST','camera.read'), ('ANALYST','observation.read'), ('ANALYST','observation.search'),
    ('ANALYST','event.read'), ('ANALYST','alert.read'), ('ANALYST','gis.read'),
    ('ANALYST','gis.coverage.read'), ('ANALYST','organization.read'), ('ANALYST','geography.read'),

    ('VIEWER','camera.read'), ('VIEWER','camera.health.read'), ('VIEWER','event.read'),
    ('VIEWER','alert.read'), ('VIEWER','gis.read'), ('VIEWER','organization.read'),
    ('VIEWER','geography.read')
)
ON CONFLICT DO NOTHING;


-- ===========================================================================
-- authorization
-- ===========================================================================
-- The authorization decision, in one place.
--
-- RBAC-LOGICAL-FLOW.md section 21 asks for a centralised `can(user, permission, resource)`.
-- It lives in SQL rather than application code for two reasons:
--
--   * section 20 requires the DATABASE QUERY to return only authorized rows, not the handler to
--     filter afterwards. A predicate the query can use has to be reachable from the query.
--   * scope resolution walks hierarchies. Doing that in C# means fetching whole subtrees per
--     request; doing it here is one recursive CTE the planner can optimise.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

-- Answers: does this user hold this permission over a resource with this ownership and location?
--
-- Scope semantics, which are easy to get subtly wrong:
--   * WITHIN a dimension, scopes are OR    -- Police OR Transport
--   * ACROSS dimensions, scopes are AND    -- Police AND Ahmedabad  (RBAC sections 10-11)
--   * A dimension the group does not constrain is unrestricted in that dimension
--   * Groups are evaluated INDEPENDENTLY (section 13). A user is never granted group A's
--     permission inside group B's scope, which is why this loops per group rather than
--     unioning permissions and scopes separately.
CREATE OR REPLACE FUNCTION has_permission(
    p_user_id               UUID,
    p_permission            VARCHAR,
    p_organization_unit_id  UUID    DEFAULT NULL,
    p_geographic_area_id    UUID    DEFAULT NULL,
    p_resource_type         VARCHAR DEFAULT NULL,
    p_resource_id           UUID    DEFAULT NULL
) RETURNS BOOLEAN
LANGUAGE plpgsql STABLE
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
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
        SELECT DISTINCT ag.id AS group_id
        FROM platform_users u
        JOIN user_groups ug      ON ug.user_id = u.id
                                AND ug.status = 'ACTIVE'
                                AND (ug.expires_at IS NULL OR ug.expires_at > now())
        JOIN access_groups ag    ON ag.id = ug.group_id AND ag.status = 'ACTIVE'
        JOIN roles r             ON r.id = ag.role_id  AND r.status = 'ACTIVE'
        JOIN role_permissions rp ON rp.role_id = r.id
        WHERE u.id = p_user_id
          AND u.status = 'ACTIVE'
          AND rp.permission_code = p_permission
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
END $$;

-- The organization units a user can reach for a permission, as a set.
--
-- This is what section 20 needs: list endpoints join against it so the database returns only
-- authorized rows, instead of fetching everything and filtering in a handler that might forget.
CREATE OR REPLACE FUNCTION authorized_org_units(p_user_id UUID, p_permission VARCHAR)
RETURNS TABLE (organization_unit_id UUID)
LANGUAGE sql STABLE
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
    SELECT DISTINCT d.id
    FROM platform_users u
    JOIN user_groups ug      ON ug.user_id = u.id
                            AND ug.status = 'ACTIVE'
                            AND (ug.expires_at IS NULL OR ug.expires_at > now())
    JOIN access_groups ag    ON ag.id = ug.group_id AND ag.status = 'ACTIVE'
    JOIN roles r             ON r.id = ag.role_id  AND r.status = 'ACTIVE'
    JOIN role_permissions rp ON rp.role_id = r.id AND rp.permission_code = p_permission
    JOIN group_scopes gs     ON gs.group_id = ag.id
    JOIN scopes s            ON s.id = gs.scope_id AND s.scope_type = 'ORGANIZATION'
    CROSS JOIN LATERAL org_unit_descendants(s.organization_unit_id) d
    WHERE u.id = p_user_id AND u.status = 'ACTIVE';
$$;

-- Whether a user's access is unrestricted by organization for a permission -- i.e. they hold it
-- through at least one group that declares no organization scope. Callers need this because an
-- empty authorized_org_units() result is ambiguous on its own: it means either "no access" or
-- "access everywhere", and confusing the two either locks out an administrator or exposes the
-- whole estate.
CREATE OR REPLACE FUNCTION has_unscoped_permission(p_user_id UUID, p_permission VARCHAR)
RETURNS BOOLEAN
LANGUAGE sql STABLE
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
    SELECT EXISTS (
        SELECT 1
        FROM platform_users u
        JOIN user_groups ug      ON ug.user_id = u.id
                                AND ug.status = 'ACTIVE'
                                AND (ug.expires_at IS NULL OR ug.expires_at > now())
        JOIN access_groups ag    ON ag.id = ug.group_id AND ag.status = 'ACTIVE'
        JOIN roles r             ON r.id = ag.role_id  AND r.status = 'ACTIVE'
        JOIN role_permissions rp ON rp.role_id = r.id AND rp.permission_code = p_permission
        WHERE u.id = p_user_id
          AND u.status = 'ACTIVE'
          AND NOT EXISTS (
              SELECT 1 FROM group_scopes gs JOIN scopes s ON s.id = gs.scope_id
              WHERE gs.group_id = ag.id AND s.scope_type = 'ORGANIZATION')
    );
$$;


-- ===========================================================================
-- federation core
-- ===========================================================================
-- Model 3 federation tables, aligned to the shared foundation.
--
-- Identity follows CAMERA-SCHEMA.md's rule throughout: an immutable UUID for the system, a
-- human-readable `code` for people. IP addresses and vendor identifiers change; identity must not.
--
-- Ownership is `organization_unit_id`, and location is `site_id` -- two independent dimensions,
-- per DEPARTMENT-SCHEMA.md and GEOGRAPHY-SCHEMA.md. Neither is stored as a name.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

-- CREATE TYPE has no IF NOT EXISTS form, so each is guarded. The runner must be safely
-- re-runnable; an unguarded CREATE TYPE aborts the whole transaction on a second run.
DO $$ BEGIN
    CREATE TYPE vendor_kind AS ENUM (
        'Onvif', 'HikvisionIsapi', 'DahuaCgi', 'MilestoneGateway', 'GenetecWebSdk', 'Simulator');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE runtime_class AS ENUM ('Managed', 'Native');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE target_state AS ENUM ('Active', 'Disabled', 'Quarantined');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

DO $$ BEGIN
    CREATE TYPE health_status AS ENUM ('Unknown', 'Healthy', 'Degraded', 'Unreachable', 'AuthFailed');
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- ---------------------------------------------------------------------------
-- Connector targets -- the unit of scale, scheduling and failover
-- ---------------------------------------------------------------------------
-- An 80,000-camera estate is a few hundred to a few thousand of these. That is what makes the
-- fleet schedulable; 80,000 would not be. See docs/ARCHITECTURE-MODEL-3.md section 1.

CREATE TABLE IF NOT EXISTS connector_target (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code                    VARCHAR(100) NOT NULL UNIQUE,

    organization_unit_id    UUID NOT NULL REFERENCES organization_units(id),
    -- Optional, and the only thing that makes a VMS geographically scopeable before Model 1's
    -- camera registry exists.
    site_id                 UUID REFERENCES sites(id),

    display_name            VARCHAR(255) NOT NULL,
    vendor                  vendor_kind   NOT NULL,
    runtime_class           runtime_class NOT NULL DEFAULT 'Managed',

    endpoint                TEXT NOT NULL,
    -- A pointer into the secret store. NEVER secret material.
    credential_reference    TEXT NOT NULL,
    verify_tls              BOOLEAN NOT NULL DEFAULT TRUE,

    state                   target_state NOT NULL DEFAULT 'Active',

    -- Per-target tuning. Vendor tolerance varies by an order of magnitude, so a single global
    -- limit would either starve the large sites or kill the small ones.
    rate_limit_per_second   DOUBLE PRECISION NOT NULL DEFAULT 5.0 CHECK (rate_limit_per_second > 0),
    rate_limit_burst        INTEGER NOT NULL DEFAULT 10 CHECK (rate_limit_burst >= 1),
    inventory_poll_seconds  INTEGER NOT NULL DEFAULT 300 CHECK (inventory_poll_seconds >= 30),
    status_poll_seconds     INTEGER NOT NULL DEFAULT 30  CHECK (status_poll_seconds >= 5),
    event_poll_seconds      INTEGER NOT NULL DEFAULT 10  CHECK (event_poll_seconds >= 1),
    max_concurrent_requests INTEGER NOT NULL DEFAULT 4 CHECK (max_concurrent_requests > 0),

    -- Flags silent inventory drift: a device returning 3 of its 128 channels otherwise looks
    -- exactly like a successful sync.
    expected_camera_count   INTEGER,

    -- Lease / ownership. Exactly one worker owns a target at a time.
    leased_by               TEXT,
    lease_expires_at        TIMESTAMPTZ,
    last_claimed_at         TIMESTAMPTZ,

    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by              UUID REFERENCES platform_users(id),
    updated_by              UUID REFERENCES platform_users(id)
);

-- The claim query's access path: unleased Active targets, oldest claim first. Partial so it
-- stays small even when most targets are healthily leased.
CREATE INDEX IF NOT EXISTS ix_target_claimable
    ON connector_target (lease_expires_at NULLS FIRST, last_claimed_at NULLS FIRST)
    WHERE state = 'Active';

CREATE INDEX IF NOT EXISTS ix_target_leased_by ON connector_target (leased_by)
    WHERE leased_by IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_target_org  ON connector_target (organization_unit_id);
CREATE INDEX IF NOT EXISTS ix_target_site ON connector_target (site_id);

-- Worker registry. Observability only: ownership is decided by the lease columns above, never
-- by this table, so a stale row here is harmless.
CREATE TABLE IF NOT EXISTS worker_node (
    worker_id       TEXT PRIMARY KEY,
    hostname        TEXT NOT NULL,
    runtime_class   runtime_class NOT NULL,
    vendor_filter   vendor_kind[],
    started_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_heartbeat  TIMESTAMPTZ NOT NULL DEFAULT now(),
    claimed_count   INTEGER NOT NULL DEFAULT 0
);

-- ---------------------------------------------------------------------------
-- Capability matrix -- probed once, read many times
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS connector_capability (
    target_id       UUID PRIMARY KEY REFERENCES connector_target(id) ON DELETE CASCADE,
    supported       INTEGER NOT NULL,       -- [Flags] Capability bitmask
    adapter_version TEXT NOT NULL,
    probed_at       TIMESTAMPTZ NOT NULL,
    notes           JSONB NOT NULL DEFAULT '{}'::jsonb
);

-- ---------------------------------------------------------------------------
-- Event cursors
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS connector_cursor (
    target_id           UUID PRIMARY KEY REFERENCES connector_target(id) ON DELETE CASCADE,
    cursor_timestamp    TIMESTAMPTZ NOT NULL,
    cursor_event_id     TEXT,
    -- Bounds gap-fill. Without it, a target down for a day returns and floods the bus with a
    -- day of backlog at full rate.
    max_lookback_hours  INTEGER NOT NULL DEFAULT 6 CHECK (max_lookback_hours > 0),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- Federated camera inventory (what the VMS knows; Model 1 owns the registry)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS federated_camera (
    target_id           UUID NOT NULL REFERENCES connector_target(id) ON DELETE CASCADE,
    native_camera_id    TEXT NOT NULL,

    -- Model 1's registry id, resolved by reconciliation. NULL until the camera is matched --
    -- and Model 1 does not exist yet, so today it is always NULL. No FK for that reason.
    camera_id           UUID,

    organization_unit_id UUID NOT NULL REFERENCES organization_units(id),
    site_id             UUID REFERENCES sites(id),

    name                TEXT,
    vendor_model        TEXT,
    firmware            TEXT,
    -- Plain columns rather than a spatial type: nothing in Model 3 queries spatially, and the
    -- CHECK constraints are what stop a swapped lat/long being stored silently. Matches `sites`.
    latitude            DECIMAL(10, 7) CHECK (latitude  BETWEEN -90  AND 90),
    longitude           DECIMAL(10, 7) CHECK (longitude BETWEEN -180 AND 180),

    is_enabled          BOOLEAN NOT NULL DEFAULT TRUE,
    is_recording        BOOLEAN,
    health              health_status NOT NULL DEFAULT 'Unknown',
    last_seen           TIMESTAMPTZ,
    stream_references   TEXT[] NOT NULL DEFAULT '{}',
    raw_reference       TEXT,

    first_seen_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (target_id, native_camera_id)
);

CREATE INDEX IF NOT EXISTS ix_camera_registry_id ON federated_camera (camera_id)
    WHERE camera_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_camera_org ON federated_camera (organization_unit_id);
-- Reconciliation backlog: cameras a VMS reports that Model 1 has never registered.
CREATE INDEX IF NOT EXISTS ix_camera_unreconciled ON federated_camera (target_id)
    WHERE camera_id IS NULL;

-- ---------------------------------------------------------------------------
-- Connector health history
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS connector_health (
    target_id            UUID NOT NULL REFERENCES connector_target(id) ON DELETE CASCADE,
    checked_at           TIMESTAMPTZ NOT NULL,
    status               health_status NOT NULL,
    latency_ms           DOUBLE PRECISION,
    camera_count         INTEGER,
    consecutive_failures INTEGER NOT NULL DEFAULT 0,
    circuit_open         BOOLEAN NOT NULL DEFAULT FALSE,
    last_error           TEXT,
    events_since_check   BIGINT NOT NULL DEFAULT 0,
    -- The most useful signal in the system: it catches a target that is silently falling behind
    -- rather than failing loudly. A connector can return 200 OK while hours behind.
    cursor_lag_seconds   DOUBLE PRECISION,
    PRIMARY KEY (target_id, checked_at)
);

-- ---------------------------------------------------------------------------
-- Normalised events -- RANGE partitioned by occurred_at
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS federation_event (
    event_id            TEXT NOT NULL,
    source_vms_id       UUID NOT NULL,
    source_event_id     TEXT,
    camera_id           TEXT NOT NULL,      -- the VMS's own channel id until reconciliation

    -- Denormalised deliberately. Correlation must not join to resolve ownership on a table
    -- taking 100-400M rows/day.
    organization_unit_id UUID NOT NULL,
    site_id             UUID,

    event_type          TEXT NOT NULL,
    vendor_event_type   TEXT,
    occurred_at         TIMESTAMPTZ NOT NULL,
    severity            TEXT NOT NULL DEFAULT 'Info',

    object_reference    TEXT,
    confidence          DOUBLE PRECISION
                        CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    -- Plain columns rather than a spatial type: nothing in Model 3 queries spatially, and the
    -- CHECK constraints are what stop a swapped lat/long being stored silently. Matches `sites`.
    latitude            DECIMAL(10, 7) CHECK (latitude  BETWEEN -90  AND 90),
    longitude           DECIMAL(10, 7) CHECK (longitude BETWEEN -180 AND 180),

    raw_reference       TEXT,
    delivery_mode       TEXT NOT NULL DEFAULT 'Live',
    trace_id            TEXT,
    received_at         TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- The partition key must participate in the primary key on a partitioned table.
    PRIMARY KEY (occurred_at, event_id)
) PARTITION BY RANGE (occurred_at);

-- Idempotency. Transport is at-least-once; with ON CONFLICT DO NOTHING this makes sinks
-- effectively-once. Nothing may assume exactly-once delivery.
--
-- Deliberately NOT partial: PostgreSQL rejects partial unique indexes on partitioned tables,
-- and the WHERE would be redundant anyway -- unique indexes are NULLS DISTINCT by default, so
-- rows with a NULL source_event_id already never conflict.
CREATE UNIQUE INDEX IF NOT EXISTS ux_event_dedup
    ON federation_event (source_vms_id, source_event_id, occurred_at);

CREATE INDEX IF NOT EXISTS ix_event_camera_time ON federation_event (camera_id, occurred_at DESC);
-- The correlation access path: same object across cameras within a time window.
CREATE INDEX IF NOT EXISTS ix_event_object_time ON federation_event (object_reference, occurred_at DESC)
    WHERE object_reference IS NOT NULL;
-- The API access path: every event query is organization-scoped and time-bounded.
CREATE INDEX IF NOT EXISTS ix_event_org_time  ON federation_event (organization_unit_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_event_type_time ON federation_event (event_type, occurred_at DESC);

-- Catch-all so an out-of-range timestamp (vendor clock skew, a bad backfill) is captured and
-- visible rather than failing the insert and losing the event.
CREATE TABLE IF NOT EXISTS federation_event_default PARTITION OF federation_event DEFAULT;

-- Dead letter: payloads that could not be normalised. Retained so a mapping bug is fixed by
-- replay rather than by re-pulling from a VMS that has aged the event out.
CREATE TABLE IF NOT EXISTS federation_event_deadletter (
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    target_id       UUID NOT NULL,
    received_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    reason          TEXT NOT NULL,
    raw_reference   TEXT,
    raw_payload     JSONB
);


-- ===========================================================================
-- secrets and audit
-- ===========================================================================
-- Credential storage, API keys, and audit.
--
-- Design notes:
--   * Secrets are encrypted in the APPLICATION with AES-256-GCM, not by pgcrypto. The key never
--     travels to PostgreSQL, never appears in a query, and never reaches the statement log. A
--     DBA with full table access still cannot read a camera password.
--   * key_id records WHICH key sealed each row, so keys rotate without a flag day.
--   * Every credential access and every configuration change is audited. For a system holding
--     access to a state's camera estate, "who repointed this NVR, and when" must be answerable.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

CREATE TABLE IF NOT EXISTS secret (
    credential_reference TEXT PRIMARY KEY,

    -- AES-256-GCM output, split rather than concatenated so a wrong-length nonce or tag is a
    -- constraint violation at write time instead of a decryption failure at 3am.
    ciphertext      BYTEA NOT NULL,
    nonce           BYTEA NOT NULL CHECK (octet_length(nonce) = 12),
    tag             BYTEA NOT NULL CHECK (octet_length(tag) = 16),

    key_id          TEXT NOT NULL,
    description     TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    rotated_at      TIMESTAMPTZ
);

-- Finds rows still sealed under a retired key, so a rotation can be driven to completion and
-- verified rather than assumed.
CREATE INDEX IF NOT EXISTS ix_secret_key_id ON secret (key_id);

-- ---------------------------------------------------------------------------
-- API keys -- machine-to-machine only. People authenticate with JWT.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS api_key (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    key_id          VARCHAR(100) NOT NULL UNIQUE,

    -- SHA-256 of the presented key. Verified by hashing and comparing, so the key itself exists
    -- only in the client's hands. A database dump must not confer the ability to write
    -- camera credentials.
    key_hash        TEXT NOT NULL,
    display_name    VARCHAR(255) NOT NULL,

    -- An API key acts through an access group, exactly as a user does. Reusing the same
    -- permission and scope machinery means there is one authorization model to reason about,
    -- not two that can drift apart.
    group_id        UUID NOT NULL REFERENCES access_groups(id),

    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by      UUID REFERENCES platform_users(id),
    expires_at      TIMESTAMPTZ,
    revoked_at      TIMESTAMPTZ,
    last_used_at    TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_api_key_hash ON api_key (key_hash)
    WHERE revoked_at IS NULL;

-- ---------------------------------------------------------------------------
-- Audit
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS credential_access_log (
    id                   BIGINT GENERATED ALWAYS AS IDENTITY,
    accessed_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    credential_reference TEXT NOT NULL,
    accessed_by          TEXT NOT NULL,
    target_id            UUID,
    succeeded            BOOLEAN NOT NULL,
    failure_reason       TEXT,
    PRIMARY KEY (accessed_at, id)
) PARTITION BY RANGE (accessed_at);

CREATE TABLE IF NOT EXISTS credential_access_log_default
    PARTITION OF credential_access_log DEFAULT;

CREATE INDEX IF NOT EXISTS ix_credential_access_ref
    ON credential_access_log (credential_reference, accessed_at DESC);

-- The alerting query: repeated failures against one reference mean either a rotated secret
-- nobody updated, or someone probing.
CREATE INDEX IF NOT EXISTS ix_credential_access_failures
    ON credential_access_log (accessed_at DESC) WHERE NOT succeeded;

CREATE TABLE IF NOT EXISTS config_audit (
    id                   BIGINT GENERATED ALWAYS AS IDENTITY,
    changed_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    actor                TEXT NOT NULL,
    actor_user_id        UUID,
    actor_api_key_id     UUID,
    action               TEXT NOT NULL,   -- create | update | delete | credential_set
    entity_type          TEXT NOT NULL,   -- connector_target | organization_unit | user | ...
    entity_id            TEXT NOT NULL,
    organization_unit_id UUID,

    -- Before/after snapshots. Credential material is NEVER included: the secret writer records
    -- that a credential changed, never what it changed to.
    before_state         JSONB,
    after_state          JSONB,

    source_address       TEXT,
    PRIMARY KEY (changed_at, id)
) PARTITION BY RANGE (changed_at);

CREATE TABLE IF NOT EXISTS config_audit_default PARTITION OF config_audit DEFAULT;

CREATE INDEX IF NOT EXISTS ix_config_audit_entity
    ON config_audit (entity_type, entity_id, changed_at DESC);
CREATE INDEX IF NOT EXISTS ix_config_audit_actor
    ON config_audit (actor, changed_at DESC);


-- ===========================================================================
-- partition maintenance
-- ===========================================================================
-- Partition maintenance for federation_event and the audit tables.
--
-- Retention is enforced by DROP PARTITION, never DELETE. At 100-400M rows/day a DELETE would
-- generate more WAL than the inserts it is cleaning up, and leave the table bloated behind an
-- autovacuum that never catches up.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

CREATE OR REPLACE FUNCTION ensure_event_partitions(
    from_date DATE DEFAULT CURRENT_DATE,
    days_ahead INTEGER DEFAULT 14
) RETURNS INTEGER
LANGUAGE plpgsql
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
DECLARE
    d          DATE;
    part_name  TEXT;
    created    INTEGER := 0;
BEGIN
    FOR i IN 0..days_ahead LOOP
        d := from_date + i;
        part_name := format('federation_event_%s', to_char(d, 'YYYYMMDD'));

        IF NOT EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = part_name AND n.nspname = 'federation'
        ) THEN
            BEGIN
                EXECUTE format(
                    'CREATE TABLE federation.%I PARTITION OF federation.federation_event '
                    'FOR VALUES FROM (%L) TO (%L)', part_name, d, d + 1);
                created := created + 1;
            EXCEPTION WHEN check_violation THEN
                -- PostgreSQL scans the DEFAULT partition when attaching a range and refuses if
                -- any existing row would belong to it. Rows land there only when a partition was
                -- missing at insert time (clock skew, a lapsed maintenance job). Say so precisely.
                RAISE EXCEPTION
                    'Cannot create partition % : federation_event_default holds rows in range '
                    '[%, %). Drain those rows into their correct partition first. '
                    'See event_partition_health.rows_in_default_partition.',
                    part_name, d, d + 1
                    USING ERRCODE = 'check_violation';
            END;
        END IF;
    END LOOP;

    RETURN created;
END $$;

-- Returns what was dropped so the caller can log it. This is irreversible data loss by design
-- and must be auditable.
CREATE OR REPLACE FUNCTION drop_event_partitions_before(cutoff DATE)
RETURNS TABLE (dropped_partition TEXT)
LANGUAGE plpgsql
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
DECLARE
    r RECORD;
BEGIN
    -- The regex filter is materialised BEFORE to_date runs. SQL does not guarantee predicate
    -- order, so a flat WHERE would let to_date('_default') be attempted and raise.
    FOR r IN
        WITH daily AS MATERIALIZED (
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation' AND c.relname ~ '^federation_event_[0-9]{8}$'
        )
        SELECT relname FROM daily WHERE to_date(right(relname, 8), 'YYYYMMDD') < cutoff
    LOOP
        EXECUTE format('DROP TABLE federation.%I', r.relname);
        dropped_partition := r.relname;
        RETURN NEXT;
    END LOOP;
END $$;

-- Surfaces the condition that silently degrades query plans: rows landing in DEFAULT because
-- their partition did not exist. Alert on this being non-zero.
CREATE OR REPLACE VIEW event_partition_health AS
SELECT
    (SELECT count(*) FROM federation_event_default) AS rows_in_default_partition,
    (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
      WHERE n.nspname = 'federation' AND c.relname ~ '^federation_event_[0-9]{8}$'
    ) AS daily_partition_count,
    (SELECT max(to_date(right(relname, 8), 'YYYYMMDD'))
     FROM (SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation' AND c.relname ~ '^federation_event_[0-9]{8}$') daily
    ) AS furthest_partition_date;

-- Audit volume is far lower than event volume, so monthly is enough. Still partitioned, because
-- audit is retained for years and an unpartitioned table nobody deletes from becomes
-- unmanageable at exactly the moment an auditor needs it.
CREATE OR REPLACE FUNCTION ensure_audit_partitions(
    from_month DATE DEFAULT date_trunc('month', CURRENT_DATE)::date,
    months_ahead INTEGER DEFAULT 3
) RETURNS INTEGER
LANGUAGE plpgsql
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
DECLARE
    m         DATE;
    created   INTEGER := 0;
    tbl       TEXT;
    part_name TEXT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY['credential_access_log', 'config_audit'] LOOP
        FOR i IN 0..months_ahead LOOP
            m := (date_trunc('month', from_month) + (i || ' month')::interval)::date;
            part_name := format('%s_%s', tbl, to_char(m, 'YYYYMM'));

            IF NOT EXISTS (
                SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relname = part_name AND n.nspname = 'federation'
            ) THEN
                EXECUTE format(
                    'CREATE TABLE federation.%I PARTITION OF federation.%I '
                    'FOR VALUES FROM (%L) TO (%L)',
                    part_name, tbl, m, (m + interval '1 month')::date);
                created := created + 1;
            END IF;
        END LOOP;
    END LOOP;

    RETURN created;
END $$;


-- ===========================================================================
-- connection tests
-- ===========================================================================
-- Asynchronous connector connection tests.
--
-- A test against a real device can take 30+ seconds, which is too long to hold an HTTP
-- connection open behind a reverse proxy. The API starts a job and the client polls.
--
-- Stored in a table rather than in memory because several API instances may run: a result must
-- be readable from an instance other than the one that produced it, and must survive a restart.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

CREATE TABLE IF NOT EXISTS connection_test (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    target_id       UUID NOT NULL REFERENCES connector_target(id) ON DELETE CASCADE,

    status          VARCHAR(20) NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending','running','completed','failed','abandoned')),

    requested_by    TEXT NOT NULL,
    requested_at    TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Which instance picked the job up. Needed to recognise work abandoned by a dead instance.
    executed_by     TEXT,
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,

    result          JSONB,
    failure_reason  TEXT
);

CREATE INDEX IF NOT EXISTS ix_connection_test_target
    ON connection_test (target_id, requested_at DESC);

-- The sweeper's access path: jobs still claimed to be running.
CREATE INDEX IF NOT EXISTS ix_connection_test_active
    ON connection_test (started_at)
    WHERE status IN ('pending', 'running');

-- Marks jobs abandoned when the executing instance died mid-test.
--
-- Without this a crashed instance leaves jobs 'running' forever and the operator's UI spins
-- with no explanation. Returning the count lets the caller log that it happened, because a
-- non-zero result means an API instance died and that is worth knowing.
CREATE OR REPLACE FUNCTION sweep_abandoned_connection_tests(stale_after INTERVAL DEFAULT '5 minutes')
RETURNS INTEGER
LANGUAGE plpgsql
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
DECLARE
    swept INTEGER;
BEGIN
    UPDATE connection_test
    SET status = 'abandoned',
        completed_at = now(),
        failure_reason = format(
            'No result after %s. The API instance running this test most likely stopped.',
            stale_after)
    WHERE status IN ('pending', 'running')
      AND COALESCE(started_at, requested_at) < now() - stale_after;

    GET DIAGNOSTICS swept = ROW_COUNT;
    RETURN swept;
END $$;


-- ===========================================================================
-- principal authorization
-- ===========================================================================
-- Scope resolution keyed on the PRINCIPAL, not on a user id.
--
-- The defect this closes: every scope predicate took a user id, and an API key has none. So
-- `authorized_org_units(NULL, ...)` returned the empty set and a scoped key saw nothing at all,
-- while an unscoped key took the `@Unscoped` branch and saw the entire estate. The only key that
-- worked was an estate-wide one, which is precisely the key nobody should be issuing.
--
-- `0006_secrets_and_audit.sql` already states the intent -- "an API key acts through an access
-- group, exactly as a user does" -- and the schema models it correctly. Only the authorization
-- functions failed to honour it, so a key's group_scopes rows were stored and then ignored.
--
-- The fix resolves a set of access groups from whichever principal is presented, and evaluates
-- scope against that set. Both principals then share one scope engine rather than a working one
-- and a broken one.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

-- The access groups a principal acts through.
--
-- Exactly one of the two ids is expected. If both are supplied, BOTH branches are excluded and
-- the result is empty: an ambiguous principal denies rather than broadens. That is deliberate --
-- a UNION of the two would let a caller who somehow carried both identities inherit the union of
-- their scopes, which is the one outcome an authorization function must never produce.
--
-- LANGUAGE sql so the planner can still inline this into the callers below; a plpgsql body would
-- become an optimisation fence in the middle of every scoped list query.
CREATE OR REPLACE FUNCTION principal_groups(p_user_id UUID, p_api_key_id UUID)
RETURNS TABLE (group_id UUID)
LANGUAGE sql STABLE
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
    -- Human principal: membership must be active and unexpired (RBAC section 26).
    SELECT ag.id
    FROM platform_users u
    JOIN user_groups ug   ON ug.user_id = u.id
                         AND ug.status = 'ACTIVE'
                         AND (ug.expires_at IS NULL OR ug.expires_at > now())
    JOIN access_groups ag ON ag.id = ug.group_id AND ag.status = 'ACTIVE'
    WHERE p_api_key_id IS NULL
      AND u.id = p_user_id
      AND u.status = 'ACTIVE'

    UNION

    -- Machine principal: the key must not be revoked or expired. Checked here as well as at
    -- authentication, so a key revoked mid-request cannot finish the request it started.
    SELECT ag.id
    FROM api_key k
    JOIN access_groups ag ON ag.id = k.group_id AND ag.status = 'ACTIVE'
    WHERE p_user_id IS NULL
      AND k.id = p_api_key_id
      AND k.revoked_at IS NULL
      AND (k.expires_at IS NULL OR k.expires_at > now());
$$;

-- The old user-only signatures are DROPPED rather than left in place. CREATE OR REPLACE with a
-- changed parameter list creates an OVERLOAD, and an overload here is worse than no fix at all:
-- any call site still passing a lone user id would keep resolving to the old function and keep
-- the hole open, silently and with a green build.
DROP FUNCTION IF EXISTS has_permission(UUID, VARCHAR, UUID, UUID, VARCHAR, UUID);
DROP FUNCTION IF EXISTS authorized_org_units(UUID, VARCHAR);
DROP FUNCTION IF EXISTS has_unscoped_permission(UUID, VARCHAR);

-- Answers: does this principal hold this permission over a resource with this ownership and
-- location?
--
-- Scope semantics, unchanged from 0004 and still easy to get subtly wrong:
--   * WITHIN a dimension, scopes are OR    -- Police OR Transport
--   * ACROSS dimensions, scopes are AND    -- Police AND Ahmedabad  (RBAC sections 10-11)
--   * A dimension the group does not constrain is unrestricted in that dimension
--   * Groups are evaluated INDEPENDENTLY (section 13). A principal is never granted group A's
--     permission inside group B's scope, which is why this loops per group rather than
--     unioning permissions and scopes separately.
CREATE OR REPLACE FUNCTION has_permission(
    p_user_id               UUID,
    p_api_key_id            UUID,
    p_permission            VARCHAR,
    p_organization_unit_id  UUID    DEFAULT NULL,
    p_geographic_area_id    UUID    DEFAULT NULL,
    p_resource_type         VARCHAR DEFAULT NULL,
    p_resource_id           UUID    DEFAULT NULL
) RETURNS BOOLEAN
LANGUAGE plpgsql STABLE
SET search_path = federation, public
AS $$
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
END $$;

-- The organization units a principal can reach for a permission, as a set.
--
-- This is what section 20 needs: list endpoints join against it so the database returns only
-- authorized rows, instead of fetching everything and filtering in a handler that might forget.
CREATE OR REPLACE FUNCTION authorized_org_units(
    p_user_id UUID, p_api_key_id UUID, p_permission VARCHAR)
RETURNS TABLE (organization_unit_id UUID)
LANGUAGE sql STABLE
SET search_path = federation, public
AS $$
    SELECT DISTINCT d.id
    FROM principal_groups(p_user_id, p_api_key_id) pg
    JOIN access_groups ag    ON ag.id = pg.group_id
    JOIN roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
    JOIN role_permissions rp ON rp.role_id = r.id AND rp.permission_code = p_permission
    JOIN group_scopes gs     ON gs.group_id = ag.id
    JOIN scopes s            ON s.id = gs.scope_id AND s.scope_type = 'ORGANIZATION'
    CROSS JOIN LATERAL org_unit_descendants(s.organization_unit_id) d;
$$;

-- Every permission a principal exercises without organization limits -- i.e. holds through at
-- least one group that declares no organization scope.
--
-- Returned as a SET, never as one flag. Collapsing it was a real vulnerability: computed for one
-- permission and then honoured as a bypass for every other, so an unscoped read silently
-- conferred unscoped administration. This is also the single definition of "unscoped" -- the
-- login path and the API key path had grown separate copies of this query, and the key's copy
-- was a per-group flag rather than a per-permission set.
CREATE OR REPLACE FUNCTION unscoped_permissions(p_user_id UUID, p_api_key_id UUID)
RETURNS TABLE (permission_code VARCHAR)
LANGUAGE sql STABLE
SET search_path = federation, public
AS $$
    SELECT DISTINCT rp.permission_code
    FROM principal_groups(p_user_id, p_api_key_id) pg
    JOIN access_groups ag    ON ag.id = pg.group_id
    JOIN roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
    JOIN role_permissions rp ON rp.role_id = r.id
    WHERE NOT EXISTS (
        SELECT 1 FROM group_scopes gs
        JOIN scopes s ON s.id = gs.scope_id
        WHERE gs.group_id = ag.id AND s.scope_type = 'ORGANIZATION');
$$;

-- Retained for callers that want the answer for one permission.
CREATE OR REPLACE FUNCTION has_unscoped_permission(
    p_user_id UUID, p_api_key_id UUID, p_permission VARCHAR)
RETURNS BOOLEAN
LANGUAGE sql STABLE
SET search_path = federation, public
AS $$
    SELECT EXISTS (
        SELECT 1 FROM unscoped_permissions(p_user_id, p_api_key_id)
        WHERE permission_code = p_permission);
$$;

-- The permissions a principal holds at all, before scope is applied.
--
-- The API key path built this inline; the login path built it inline too. One definition means
-- a role or status rule added later cannot apply to people and quietly miss machines.
CREATE OR REPLACE FUNCTION principal_permissions(p_user_id UUID, p_api_key_id UUID)
RETURNS TABLE (permission_code VARCHAR)
LANGUAGE sql STABLE
SET search_path = federation, public
AS $$
    SELECT DISTINCT rp.permission_code
    FROM principal_groups(p_user_id, p_api_key_id) pg
    JOIN access_groups ag    ON ag.id = pg.group_id
    JOIN roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
    JOIN role_permissions rp ON rp.role_id = r.id;
$$;


-- ===========================================================================
-- retention
-- ===========================================================================
-- Retention. Partitions were being created and never dropped.
--
-- `0007_partition_maintenance.sql` built `drop_event_partitions_before` and nothing ever called
-- it, so `federation_event` grew without bound at 100-400M rows/day. `connector_health` was
-- worse: not partitioned at all, and written every StatusPollInterval (30s default) per target,
-- which is ~5.8M rows/day at 2,000 VMS instances with no way to remove any of it short of a
-- DELETE that would outrun autovacuum.
--
-- Three things happen here:
--   1. connector_health becomes RANGE partitioned by day, like federation_event.
--   2. Every high-volume table gets a drop-partition retention function.
--   3. A claim table lets exactly one API instance run retention per day, since every instance
--      runs the same maintenance loop.
--
-- The low-volume tables (connection_test, dead letter) are purged with DELETE deliberately. The
-- DROP PARTITION rule exists because DELETE cannot keep up at event volume; it is not a reason
-- to partition a table that takes a few thousand rows a month.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

-- ---------------------------------------------------------------------------
-- Daily job claim
-- ---------------------------------------------------------------------------

-- Retention is irreversible, so it must run once per day across the whole fleet rather than once
-- per instance per day. The claim is the row update itself: whichever instance wins the
-- conditional UPDATE runs the job, and the losers get no row back and skip. No advisory lock,
-- and no leader election -- the same reasoning as the connector leases in architecture section 4.
CREATE TABLE IF NOT EXISTS maintenance_run (
    job            TEXT PRIMARY KEY,
    last_run_date  DATE NOT NULL,
    last_run_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    detail         TEXT
);

CREATE OR REPLACE FUNCTION try_claim_daily_job(p_job TEXT, p_today DATE)
RETURNS BOOLEAN
LANGUAGE plpgsql
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
DECLARE
    claimed BOOLEAN;
BEGIN
    INSERT INTO maintenance_run (job, last_run_date)
    VALUES (p_job, p_today)
    ON CONFLICT (job) DO UPDATE
        SET last_run_date = EXCLUDED.last_run_date,
            last_run_at   = now()
        WHERE maintenance_run.last_run_date < EXCLUDED.last_run_date
    RETURNING TRUE INTO claimed;

    RETURN COALESCE(claimed, FALSE);
END $$;

CREATE OR REPLACE FUNCTION record_job_detail(p_job TEXT, p_detail TEXT)
RETURNS VOID
LANGUAGE sql
SET search_path = federation, public
AS $$
    UPDATE maintenance_run SET detail = p_detail WHERE job = p_job;
$$;

-- ---------------------------------------------------------------------------
-- connector_health becomes partitioned
-- ---------------------------------------------------------------------------

-- Converted in place, preserving rows. An install that already has health history keeps it: the
-- daily partitions covering the existing range are created BEFORE the copy, because attaching a
-- range partition over rows sitting in DEFAULT is refused by PostgreSQL.
DO $$
DECLARE
    min_d DATE;
    max_d DATE;
    d     DATE;
BEGIN
    -- relkind 'r' means an ordinary table, i.e. not yet converted. Re-running is a no-op.
    IF NOT EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'federation' AND c.relname = 'connector_health' AND c.relkind = 'r'
    ) THEN
        RETURN;
    END IF;

    ALTER TABLE connector_health RENAME TO connector_health_legacy;
    ALTER TABLE connector_health_legacy RENAME CONSTRAINT connector_health_pkey
        TO connector_health_legacy_pkey;

    CREATE TABLE connector_health (
        target_id            UUID NOT NULL REFERENCES connector_target(id) ON DELETE CASCADE,
        checked_at           TIMESTAMPTZ NOT NULL,
        status               health_status NOT NULL,
        latency_ms           DOUBLE PRECISION,
        camera_count         INTEGER,
        consecutive_failures INTEGER NOT NULL DEFAULT 0,
        circuit_open         BOOLEAN NOT NULL DEFAULT FALSE,
        last_error           TEXT,
        events_since_check   BIGINT NOT NULL DEFAULT 0,
        -- The most useful signal in the system: it catches a target that is silently falling
        -- behind rather than failing loudly. A connector can return 200 OK while hours behind.
        cursor_lag_seconds   DOUBLE PRECISION,
        PRIMARY KEY (target_id, checked_at)
    ) PARTITION BY RANGE (checked_at);

    -- Catches rows whose partition was missing rather than rejecting the insert. A health write
    -- failing would take down the very reporting that says a target is unwell.
    CREATE TABLE connector_health_default PARTITION OF connector_health DEFAULT;

    SELECT min(checked_at)::date, max(checked_at)::date
    INTO min_d, max_d FROM connector_health_legacy;

    IF min_d IS NOT NULL THEN
        d := min_d;
        WHILE d <= max_d LOOP
            EXECUTE format(
                'CREATE TABLE federation.%I PARTITION OF federation.connector_health '
                'FOR VALUES FROM (%L) TO (%L)',
                format('connector_health_%s', to_char(d, 'YYYYMMDD')), d, d + 1);
            d := d + 1;
        END LOOP;

        INSERT INTO connector_health SELECT * FROM connector_health_legacy;
    END IF;

    DROP TABLE connector_health_legacy;
END $$;

CREATE OR REPLACE FUNCTION ensure_health_partitions(
    from_date DATE DEFAULT CURRENT_DATE,
    days_ahead INTEGER DEFAULT 14
) RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    d         DATE;
    part_name TEXT;
    created   INTEGER := 0;
BEGIN
    FOR i IN 0..days_ahead LOOP
        d := from_date + i;
        part_name := format('connector_health_%s', to_char(d, 'YYYYMMDD'));

        IF NOT EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = part_name AND n.nspname = 'federation'
        ) THEN
            BEGIN
                EXECUTE format(
                    'CREATE TABLE federation.%I PARTITION OF federation.connector_health '
                    'FOR VALUES FROM (%L) TO (%L)', part_name, d, d + 1);
                created := created + 1;
            EXCEPTION WHEN check_violation THEN
                RAISE EXCEPTION
                    'Cannot create partition % : connector_health_default holds rows in range '
                    '[%, %). Drain those rows into their correct partition first.',
                    part_name, d, d + 1
                    USING ERRCODE = 'check_violation';
            END;
        END IF;
    END LOOP;

    RETURN created;
END $$;

-- ---------------------------------------------------------------------------
-- Retention
-- ---------------------------------------------------------------------------

-- Redefined from 0007 to add the cutoff guard. A cutoff after today would drop the partition
-- currently being written to and every one created ahead of need -- an outage and unrecoverable
-- data loss from a single mistyped retention setting. The application validates its
-- configuration too; this is the backstop that holds even when the caller is psql.
CREATE OR REPLACE FUNCTION drop_event_partitions_before(cutoff DATE)
RETURNS TABLE (dropped_partition TEXT)
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    r RECORD;
BEGIN
    IF cutoff IS NULL OR cutoff > CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing retention cutoff % : it is not in the past, so it would drop the '
            'partition being written to right now.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- The regex filter is materialised BEFORE to_date runs. SQL does not guarantee predicate
    -- order, so a flat WHERE would let to_date('_default') be attempted and raise.
    FOR r IN
        WITH daily AS MATERIALIZED (
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation' AND c.relname ~ '^federation_event_[0-9]{8}$'
        )
        SELECT relname FROM daily WHERE to_date(right(relname, 8), 'YYYYMMDD') < cutoff
    LOOP
        EXECUTE format('DROP TABLE federation.%I', r.relname);
        dropped_partition := r.relname;
        RETURN NEXT;
    END LOOP;
END $$;

CREATE OR REPLACE FUNCTION drop_health_partitions_before(cutoff DATE)
RETURNS TABLE (dropped_partition TEXT)
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    r RECORD;
BEGIN
    IF cutoff IS NULL OR cutoff > CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing retention cutoff % : it is not in the past.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    FOR r IN
        WITH daily AS MATERIALIZED (
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation' AND c.relname ~ '^connector_health_[0-9]{8}$'
        )
        SELECT relname FROM daily WHERE to_date(right(relname, 8), 'YYYYMMDD') < cutoff
    LOOP
        EXECUTE format('DROP TABLE federation.%I', r.relname);
        dropped_partition := r.relname;
        RETURN NEXT;
    END LOOP;
END $$;

-- config_audit and credential_access_log are monthly, and retained for years rather than days.
-- Their cutoff is a month boundary: a partition is dropped only once the WHOLE month it covers
-- is older than the cutoff, so a 24-month policy never removes a record that is 23 months old.
CREATE OR REPLACE FUNCTION drop_audit_partitions_before(cutoff DATE)
RETURNS TABLE (dropped_partition TEXT)
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    r RECORD;
BEGIN
    IF cutoff IS NULL OR cutoff > CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing retention cutoff % : it is not in the past.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    FOR r IN
        WITH monthly AS MATERIALIZED (
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation'
              AND c.relname ~ '^(config_audit|credential_access_log)_[0-9]{6}$'
        )
        SELECT relname FROM monthly
        -- The month AFTER the one this partition covers must still be at or before the cutoff.
        WHERE (to_date(right(relname, 6), 'YYYYMM') + interval '1 month')::date <= cutoff
    LOOP
        EXECUTE format('DROP TABLE federation.%I', r.relname);
        dropped_partition := r.relname;
        RETURN NEXT;
    END LOOP;
END $$;

-- Low-volume tables. DELETE is correct here: a few thousand rows a month is nothing for
-- autovacuum, and partitioning them would add machinery that buys nothing.
CREATE OR REPLACE FUNCTION purge_connection_tests_before(cutoff TIMESTAMPTZ)
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    removed INTEGER;
BEGIN
    -- Only terminal jobs. A test still pending or running is not old, however long ago it was
    -- requested -- deleting one would leave a client polling an id that no longer exists.
    DELETE FROM connection_test
    WHERE requested_at < cutoff
      AND status IN ('completed', 'failed', 'abandoned');

    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END $$;

CREATE OR REPLACE FUNCTION purge_deadletter_before(cutoff TIMESTAMPTZ)
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    removed INTEGER;
BEGIN
    DELETE FROM federation_event_deadletter WHERE received_at < cutoff;
    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END $$;

-- ---------------------------------------------------------------------------
-- Visibility
-- ---------------------------------------------------------------------------

-- What retention is actually holding, so "how far back can we search?" has an answer that comes
-- from the database rather than from the configuration file someone believes is deployed.
CREATE OR REPLACE VIEW retention_status AS
WITH parts AS (
    SELECT c.relname,
           CASE
               WHEN c.relname ~ '^federation_event_[0-9]{8}$'   THEN 'federation_event'
               WHEN c.relname ~ '^connector_health_[0-9]{8}$'   THEN 'connector_health'
               WHEN c.relname ~ '^config_audit_[0-9]{6}$'       THEN 'config_audit'
               WHEN c.relname ~ '^credential_access_log_[0-9]{6}$' THEN 'credential_access_log'
           END AS table_name,
           CASE
               WHEN c.relname ~ '[0-9]{8}$' THEN to_date(right(c.relname, 8), 'YYYYMMDD')
               ELSE to_date(right(c.relname, 6), 'YYYYMM')
           END AS covers_from,
           pg_total_relation_size(c.oid) AS bytes
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'federation'
      AND (c.relname ~ '^federation_event_[0-9]{8}$'
        OR c.relname ~ '^connector_health_[0-9]{8}$'
        OR c.relname ~ '^config_audit_[0-9]{6}$'
        OR c.relname ~ '^credential_access_log_[0-9]{6}$')
)
SELECT table_name,
       count(*)              AS partitions,
       min(covers_from)      AS oldest_data,
       max(covers_from)      AS newest_partition,
       pg_size_pretty(sum(bytes)) AS total_size
FROM parts
GROUP BY table_name;


-- ===========================================================================
-- geographic scope
-- ===========================================================================
-- The geographic half of scope resolution.
--
-- `authorized_org_units` existed from 0004 and every VMS/event query used it. There was no
-- equivalent for geography, so the organization and geography repositories had nothing to scope
-- against and simply returned everything: a user whose group was scoped to one department could
-- list every organization, every unit, every area and every site in the estate.
--
-- RBAC-LOGICAL-FLOW.md treats organization and geography as two independent dimensions, ANDed
-- together. Only one of them was enforceable.

-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

-- The geographic areas a principal can reach for a permission, expanded through the hierarchy.
--
-- A scope names ONE area and everything beneath it follows, per GEOGRAPHY-SCHEMA.md: a scope of
-- "Ahmedabad District" already covers a camera in a village three levels below. Scope rows must
-- never enumerate descendants, because a village added tomorrow would then grant nobody access
-- until every affected row was found and updated -- and nothing would report the omission.
CREATE OR REPLACE FUNCTION authorized_geographic_areas(
    p_user_id UUID, p_api_key_id UUID, p_permission VARCHAR)
RETURNS TABLE (geographic_area_id UUID)
LANGUAGE sql STABLE
-- Pinned so the function resolves its own tables regardless of the
-- caller's search_path. Without this a function works from a psql session that set
-- the path and fails from the application, which connects with the default.
SET search_path = federation, public
AS $$
    SELECT DISTINCT d.id
    FROM principal_groups(p_user_id, p_api_key_id) pg
    JOIN access_groups ag    ON ag.id = pg.group_id
    JOIN roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
    JOIN role_permissions rp ON rp.role_id = r.id AND rp.permission_code = p_permission
    JOIN group_scopes gs     ON gs.group_id = ag.id
    JOIN scopes s            ON s.id = gs.scope_id AND s.scope_type = 'GEOGRAPHY'
    CROSS JOIN LATERAL geographic_area_descendants(s.geographic_area_id) d;
$$;

-- Whether a principal's access is unrestricted by GEOGRAPHY for a permission.
--
-- Separate from has_unscoped_permission, which answers the same question for organization. The
-- two dimensions are independent: a group may be confined to one department while reaching every
-- district, or the reverse. Collapsing them into one "is unscoped" flag would grant whichever
-- dimension happened to be unconstrained across the one that was not.
CREATE OR REPLACE FUNCTION has_unscoped_geography(
    p_user_id UUID, p_api_key_id UUID, p_permission VARCHAR)
RETURNS BOOLEAN
LANGUAGE sql STABLE
SET search_path = federation, public
AS $$
    SELECT EXISTS (
        SELECT 1
        FROM principal_groups(p_user_id, p_api_key_id) pg
        JOIN access_groups ag    ON ag.id = pg.group_id
        JOIN roles r             ON r.id = ag.role_id AND r.status = 'ACTIVE'
        JOIN role_permissions rp ON rp.role_id = r.id AND rp.permission_code = p_permission
        WHERE NOT EXISTS (
            SELECT 1 FROM group_scopes gs
            JOIN scopes s ON s.id = gs.scope_id
            WHERE gs.group_id = ag.id AND s.scope_type = 'GEOGRAPHY')
    );
$$;

-- The organizations a principal can reach, derived from the units they can reach.
--
-- An organization is visible when the principal reaches at least one unit inside it. There is no
-- separate organization-level scope: scopes name units, and the organization follows from them.
CREATE OR REPLACE FUNCTION authorized_organizations(
    p_user_id UUID, p_api_key_id UUID, p_permission VARCHAR)
RETURNS TABLE (organization_id UUID)
LANGUAGE sql STABLE
SET search_path = federation, public
AS $$
    SELECT DISTINCT ou.organization_id
    FROM authorized_org_units(p_user_id, p_api_key_id, p_permission) a
    JOIN organization_units ou ON ou.id = a.organization_unit_id;
$$;


-- ####################################################################
-- ##  versions/v1.1.sql
-- ####################################################################

-- ===========================================================================
-- v1.1 — Camera status history
-- ===========================================================================
--
-- Adds a transition log for per-camera status, and the "current status since"
-- stamp that goes with it.
--
-- WHY TRANSITIONS AND NOT SAMPLES
--
-- The status poll runs every 30 seconds per target. At the 80,000 camera design
-- point, one row per camera per poll is 2,667 rows/second — roughly 230M
-- rows/day, the same order as federation_event, for data that changes a few
-- thousand times a day at most. So a row is written only when the status
-- actually changes, and it carries both the previous and the new value.
--
-- The status at any past instant is then the last transition at or before it.
-- What is deliberately NOT recoverable from this table is "was the camera being
-- polled at 10:31?" — that question belongs to connector_health, which records
-- the target's poll cadence, and to federated_camera.last_seen.
--
-- Requires: v1.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Current status, independent of history retention
-- ---------------------------------------------------------------------------

-- Without this, "Healthy since 3 March" is only answerable while the transition
-- that says so is still inside the retention window. A camera that has been
-- healthy for a year would report no known transition at all, which reads as
-- "never seen" rather than "stable for a year".
ALTER TABLE federated_camera
    ADD COLUMN IF NOT EXISTS status_changed_at TIMESTAMPTZ;

COMMENT ON COLUMN federated_camera.status_changed_at IS
    'When health, is_enabled or is_recording last changed. Survives history retention.';

-- ---------------------------------------------------------------------------
-- Transition log — RANGE partitioned by changed_at
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS camera_status_history (
    target_id           UUID NOT NULL,
    native_camera_id    TEXT NOT NULL,
    changed_at          TIMESTAMPTZ NOT NULL,

    -- Denormalised from federated_camera at write time, for the same reason
    -- federation_event denormalises it: scoping a history query must not join
    -- back to the camera table, and the answer must remain correct even after
    -- the camera row is gone.
    organization_unit_id UUID NOT NULL,
    site_id             UUID,

    -- NULL previous values mean this is the camera's first observed status,
    -- not that the previous value was unknown.
    previous_health     health_status,
    health              health_status NOT NULL,
    previous_enabled    BOOLEAN,
    is_enabled          BOOLEAN NOT NULL,
    previous_recording  BOOLEAN,
    is_recording        BOOLEAN,

    -- The partition key must participate in the primary key on a partitioned
    -- table. A camera cannot transition twice at the same instant: both writes
    -- come from one statement's trigger, in order.
    PRIMARY KEY (changed_at, target_id, native_camera_id)
) PARTITION BY RANGE (changed_at);

-- No foreign key to federated_camera on purpose. A target removed under
-- ON DELETE CASCADE would take its whole status history with it — exactly the
-- record an incident review wants after a site is decommissioned. The columns
-- above are self-sufficient without the parent row.

-- Catches rows whose partition is missing rather than rejecting the insert. The
-- trigger fires inside the camera upsert, so a failure here would take down the
-- inventory write — losing current status to protect history is the wrong way
-- round.
CREATE TABLE IF NOT EXISTS camera_status_history_default
    PARTITION OF camera_status_history DEFAULT;

-- The query this table exists for: one camera's timeline, newest first.
CREATE INDEX IF NOT EXISTS ix_camera_status_history_camera
    ON camera_status_history (target_id, native_camera_id, changed_at DESC);

-- "What broke across this department in the last hour", which is the fleet view
-- rather than the per-camera one.
CREATE INDEX IF NOT EXISTS ix_camera_status_history_org
    ON camera_status_history (organization_unit_id, changed_at DESC);

-- ---------------------------------------------------------------------------
-- Recording the transition
-- ---------------------------------------------------------------------------

-- A trigger rather than application code, because the camera upsert arrives as
-- one binary COPY plus one merge covering a whole target's inventory. Detecting
-- changes in C# would mean reading current state back first — a round trip per
-- poll — and would silently miss any write that did not go through that path
-- (the admin tool, a manual correction in psql).
CREATE OR REPLACE FUNCTION record_camera_status_change() RETURNS TRIGGER
LANGUAGE plpgsql
-- Pinned so the function resolves its own tables regardless of the caller's
-- search_path, matching every other function in this schema.
SET search_path = federation, public
AS $$
BEGIN
    INSERT INTO camera_status_history (
        target_id, native_camera_id, changed_at,
        organization_unit_id, site_id,
        previous_health, health,
        previous_enabled, is_enabled,
        previous_recording, is_recording)
    VALUES (
        NEW.target_id, NEW.native_camera_id, NEW.updated_at,
        NEW.organization_unit_id, NEW.site_id,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.health      END, NEW.health,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.is_enabled  END, NEW.is_enabled,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.is_recording END, NEW.is_recording)
    -- Two writes landing on the same camera at the same instant is a redelivery,
    -- not a second transition. Dropping the duplicate keeps the trigger from
    -- turning an idempotent upsert into a failing one.
    ON CONFLICT DO NOTHING;

    RETURN NULL;
END $$;

-- INSERT and UPDATE are separate triggers, not one AFTER INSERT OR UPDATE.
-- A trigger WHEN clause may only reference OLD and NEW column values: TG_OP is a
-- PL/pgSQL variable available inside the function body and nowhere else, and OLD
-- is not valid in an INSERT trigger's WHEN at all. A combined trigger carrying
-- the condition below fails at CREATE TRIGGER time.
DROP TRIGGER IF EXISTS trg_camera_status_history ON federated_camera;
DROP TRIGGER IF EXISTS trg_camera_status_history_insert ON federated_camera;
DROP TRIGGER IF EXISTS trg_camera_status_history_update ON federated_camera;

-- Every first observation is a transition: there is no previous status to
-- compare against, so there is no condition to test.
CREATE TRIGGER trg_camera_status_history_insert
    AFTER INSERT ON federated_camera
    FOR EACH ROW
    EXECUTE FUNCTION record_camera_status_change();

-- IS DISTINCT FROM, not <>: is_recording is nullable, and <> against NULL is
-- NULL, so a camera going from "recording" to "unknown" would produce no row.
--
-- The WHEN clause is evaluated before the function body, which is what makes
-- this affordable: the status poll updates every camera every 30 seconds, and
-- all but a handful of those updates change nothing and never enter the
-- function at all.
CREATE TRIGGER trg_camera_status_history_update
    AFTER UPDATE ON federated_camera
    FOR EACH ROW
    WHEN (
        OLD.health       IS DISTINCT FROM NEW.health
        OR OLD.is_enabled   IS DISTINCT FROM NEW.is_enabled
        OR OLD.is_recording IS DISTINCT FROM NEW.is_recording
    )
    EXECUTE FUNCTION record_camera_status_change();

-- Keeps federated_camera.status_changed_at in step with the log above. A BEFORE
-- trigger so the value is written as part of the same row write rather than as
-- a second UPDATE, which would re-fire the AFTER trigger and log a phantom
-- transition.
CREATE OR REPLACE FUNCTION stamp_camera_status_changed_at() RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
BEGIN
    NEW.status_changed_at := NEW.updated_at;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_camera_status_changed_at ON federated_camera;
DROP TRIGGER IF EXISTS trg_camera_status_changed_at_insert ON federated_camera;
DROP TRIGGER IF EXISTS trg_camera_status_changed_at_update ON federated_camera;

CREATE TRIGGER trg_camera_status_changed_at_insert
    BEFORE INSERT ON federated_camera
    FOR EACH ROW
    EXECUTE FUNCTION stamp_camera_status_changed_at();

CREATE TRIGGER trg_camera_status_changed_at_update
    BEFORE UPDATE ON federated_camera
    FOR EACH ROW
    WHEN (
        OLD.health       IS DISTINCT FROM NEW.health
        OR OLD.is_enabled   IS DISTINCT FROM NEW.is_enabled
        OR OLD.is_recording IS DISTINCT FROM NEW.is_recording
    )
    EXECUTE FUNCTION stamp_camera_status_changed_at();

-- Existing rows have a current status but no recorded transition. Stamping them
-- with their last update is the honest reading: it is when we last wrote that
-- status, and leaving NULL would make every pre-upgrade camera look unobserved.
UPDATE federated_camera
   SET status_changed_at = updated_at
 WHERE status_changed_at IS NULL;

-- ---------------------------------------------------------------------------
-- Partition maintenance
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION ensure_camera_status_partitions(
    from_date DATE DEFAULT CURRENT_DATE,
    days_ahead INTEGER DEFAULT 14
) RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    d         DATE;
    part_name TEXT;
    created   INTEGER := 0;
BEGIN
    FOR i IN 0..days_ahead LOOP
        d := from_date + i;
        part_name := format('camera_status_history_%s', to_char(d, 'YYYYMMDD'));

        IF NOT EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = part_name AND n.nspname = 'federation'
        ) THEN
            BEGIN
                EXECUTE format(
                    'CREATE TABLE federation.%I PARTITION OF federation.camera_status_history '
                    'FOR VALUES FROM (%L) TO (%L)', part_name, d, d + 1);
                created := created + 1;
            EXCEPTION WHEN check_violation THEN
                RAISE EXCEPTION
                    'Cannot create partition % : camera_status_history_default holds rows in '
                    'range [%, %). Drain those rows into their correct partition first.',
                    part_name, d, d + 1
                    USING ERRCODE = 'check_violation';
            END;
        END IF;
    END LOOP;

    RETURN created;
END $$;

-- ---------------------------------------------------------------------------
-- Retention
-- ---------------------------------------------------------------------------

-- The cutoff guard matches drop_event_partitions_before: a cutoff at or after
-- today would drop the partition currently being written to, plus every one
-- created ahead of need. Immediate, irreversible, and from a single mistyped
-- setting. The application validates its configuration too; this is the
-- backstop that holds even when the caller is psql.
CREATE OR REPLACE FUNCTION drop_camera_status_partitions_before(cutoff DATE)
RETURNS TABLE (dropped_partition TEXT)
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    part   RECORD;
    bound  DATE;
BEGIN
    IF cutoff IS NULL OR cutoff >= CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing to drop camera status partitions with cutoff % : it is not in the past. '
            'That would drop the partition being written to right now.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    FOR part IN
        SELECT c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'federation'
          AND c.relname ~ '^camera_status_history_[0-9]{8}$'
    LOOP
        bound := to_date(right(part.relname, 8), 'YYYYMMDD');

        IF bound < cutoff THEN
            EXECUTE format('DROP TABLE federation.%I', part.relname);
            dropped_partition := part.relname;
            RETURN NEXT;
        END IF;
    END LOOP;
END $$;

-- ---------------------------------------------------------------------------
-- Partitions for today onward, so the first write after this upgrade lands in a
-- real partition rather than the default.
-- ---------------------------------------------------------------------------

SELECT ensure_camera_status_partitions(CURRENT_DATE, 14);


-- ####################################################################
-- ##  versions/v1.2.sql
-- ####################################################################

-- ===========================================================================
-- v1.2 — Detection ingest, watchlist and AI-worker health
-- ===========================================================================
--
-- Adds the storage side for Model 2's capture-and-inference worker
-- (ai-worker/, Python): the sink for POST /api/v1/detections, a watchlist
-- keyed on plate number that ingest matches against, and a small table for
-- the worker's own heartbeat.
--
-- WHY A SEPARATE detection_event RATHER THAN REUSING federation_event
--
-- federation_event is Model 3's normalised VMS-event stream, keyed on
-- (source_vms_id, source_event_id) — a VMS's own notion of an event.
-- Detections come from Model 2's inference, not from a VMS, and carry a
-- different shape (vehicle/plate attributes, no vendor_event_type). Folding
-- them into federation_event would mean either nullable vendor-only columns
-- on every VMS event, or optional detection columns on every VMS row —
-- both worse than a second table with the same partitioning shape.
--
-- WHY NO DAILY PARTITION-MAINTENANCE FUNCTION (unlike camera_status_history)
--
-- Detection volume at the current rollout size (100+ cameras, 2 fps sampled)
-- does not approach federation_event's 100-400M rows/day scale yet. A single
-- DEFAULT partition is enough for now; range partitioning by occurred_at is
-- still declared so a time-bounded partition scheme can be dropped in later
-- without a table rewrite, exactly as federation_event already does.
--
-- Requires: v1.sql, v1.1.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Detection ingest
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS detection_event (
    event_id             TEXT NOT NULL,      -- worker-generated, e.g. "evt-<uuid4hex>"
    occurred_at          TIMESTAMPTZ NOT NULL,

    target_id            UUID NOT NULL REFERENCES connector_target(id) ON DELETE CASCADE,
    native_camera_id     TEXT NOT NULL,
    -- Model 1's registry id. NULL until reconciliation matches this camera to a registered
    -- one — same convention as federated_camera.camera_id.
    camera_id            UUID,

    -- Denormalised deliberately, matching federation_event: scoping a query must not join to
    -- resolve ownership.
    organization_unit_id UUID NOT NULL REFERENCES organization_units(id),
    site_id              UUID REFERENCES sites(id),

    event_type           TEXT NOT NULL,      -- "VEHICLE_DETECTED" | "ANPR_DETECTED"
    confidence           DOUBLE PRECISION
                         CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),

    vehicle_type          TEXT,
    plate_number_raw       TEXT,
    plate_number_normalized TEXT,

    -- An opaque reference, never inlined image bytes — Model 3's "video is source, metadata
    -- is product" rule applies here too. A relative disk path today; whatever the evidence
    -- storage location resolves to.
    snapshot_reference    TEXT,

    received_at           TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (occurred_at, event_id)
) PARTITION BY RANGE (occurred_at);

-- Idempotency (CLAUDE.md non-negotiable #5: every sink idempotent on
-- (source_vms, source_event_id)). Transport is at-least-once; ON CONFLICT DO NOTHING makes
-- this sink effectively-once.
CREATE UNIQUE INDEX IF NOT EXISTS ux_detection_event_dedup
    ON detection_event (event_id, occurred_at);

CREATE INDEX IF NOT EXISTS ix_detection_camera_time
    ON detection_event (target_id, native_camera_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_detection_org_time
    ON detection_event (organization_unit_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_detection_plate
    ON detection_event (plate_number_normalized, occurred_at DESC)
    WHERE plate_number_normalized IS NOT NULL;

CREATE TABLE IF NOT EXISTS detection_event_default PARTITION OF detection_event DEFAULT;

-- ---------------------------------------------------------------------------
-- Watchlist
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS watchlist_entry (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_unit_id    UUID NOT NULL REFERENCES organization_units(id),
    plate_number_normalized TEXT NOT NULL,
    reason                  TEXT,
    severity                TEXT NOT NULL DEFAULT 'Medium'
                            CHECK (severity IN ('Low', 'Medium', 'High', 'Critical')),
    is_active               BOOLEAN NOT NULL DEFAULT TRUE,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by              UUID REFERENCES platform_users(id)
);

-- One active entry per plate per organization. A revoked/deactivated entry does not block a
-- new one being raised for the same plate later.
CREATE UNIQUE INDEX IF NOT EXISTS ux_watchlist_active
    ON watchlist_entry (organization_unit_id, plate_number_normalized)
    WHERE is_active;

CREATE TABLE IF NOT EXISTS watchlist_alert (
    id                     UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    watchlist_entry_id     UUID NOT NULL REFERENCES watchlist_entry(id) ON DELETE CASCADE,
    detection_event_id     TEXT NOT NULL,
    detection_occurred_at  TIMESTAMPTZ NOT NULL,
    raised_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    acknowledged_at        TIMESTAMPTZ,
    acknowledged_by        UUID REFERENCES platform_users(id),

    FOREIGN KEY (detection_occurred_at, detection_event_id)
        REFERENCES detection_event (occurred_at, event_id) ON DELETE CASCADE
);

-- A detection is matched against the watchlist at most once — a retried/duplicate ingest
-- POST must not raise a second alert for the same match.
CREATE UNIQUE INDEX IF NOT EXISTS ux_watchlist_alert_dedup
    ON watchlist_alert (watchlist_entry_id, detection_event_id);

CREATE INDEX IF NOT EXISTS ix_watchlist_alert_raised
    ON watchlist_alert (raised_at DESC);

-- ---------------------------------------------------------------------------
-- AI-worker health
-- ---------------------------------------------------------------------------

-- Deliberately not worker_node: that table is the leased-connector-worker registry (Model 3's
-- own workers, claimed/released against connector targets). An AI worker owns a static camera
-- partition (WORKER_INDEX/WORKER_COUNT) rather than a lease, so it gets its own small table
-- instead of overloading a different lifecycle's schema.
CREATE TABLE IF NOT EXISTS ai_worker_health (
    worker_id           TEXT PRIMARY KEY,
    hostname             TEXT,
    first_seen_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_heartbeat_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------------
-- Permissions
-- ---------------------------------------------------------------------------

-- observation.read/observation.search and alert.read/alert.acknowledge already exist (v1.sql)
-- and are reused as-is for reading detections and watchlist alerts. Only the write-side
-- capabilities are genuinely new.
INSERT INTO permissions (code, name, category, description) VALUES
    ('observation.write', 'Submit observations', 'analytics', 'Ingest Model 2 detection events'),
    ('watchlist.manage',  'Manage watchlist',    'alerts',    'Create and deactivate watchlist entries'),
    ('worker.heartbeat',  'Report worker health','vms',       'Submit AI-worker heartbeats')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;


-- ####################################################################
-- ##  versions/v1.3.sql
-- ####################################################################

-- ===========================================================================
-- v1.3 — API key lifecycle
-- ===========================================================================
--
-- Adds the read/revoke side of the machine-to-machine API key feature. Create
-- already shipped (POST /api/v1/api-keys, on group.manage). This version:
--
--   * gives API keys their own permission pair rather than borrowing
--     group.manage, so key issuance can be delegated without also handing over
--     access-group editing;
--   * records WHO revoked a key, next to WHEN (revoked_at was already here,
--     unused).
--
-- The API moves all three api-key endpoints onto apikey.manage / apikey.read in
-- the same change, so create is never on a different permission from list and
-- revoke.
--
-- Requires: v1.sql, v1.1.sql, v1.2.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Permissions
-- ---------------------------------------------------------------------------

-- Issuing a credential another system authenticates with is a distinct, and
-- arguably higher, privilege than editing an access group's scopes. A separate
-- permission lets an operator delegate key rotation without conferring RBAC
-- editing, and keeps the audit query for "who touched API keys" exact.
INSERT INTO permissions (code, name, category, description) VALUES
    ('apikey.read',   'View API keys',   'admin', 'List machine-to-machine API keys and their lifecycle state'),
    ('apikey.manage', 'Manage API keys', 'admin', 'Provision and revoke machine-to-machine API keys')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN receives every permission, including any added by a later version
-- (v1.sql seeds this with a CROSS JOIN, which only ran against the permissions
-- that existed then). Re-run for the two just added.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN' AND p.code IN ('apikey.read', 'apikey.manage')
ON CONFLICT DO NOTHING;

-- STATE_ADMIN holds group.manage today and is the role the create endpoint was
-- reachable through, so it keeps the capability unbroken across this change.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN', 'apikey.read'),
    ('STATE_ADMIN', 'apikey.manage')
)
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- api_key.revoked_by
-- ---------------------------------------------------------------------------

-- config_audit already records the actor of a revoke, but a self-describing row
-- keeps the credential's own history readable without a join to the audit log,
-- and matches created_by, which is already here.
ALTER TABLE api_key
    ADD COLUMN IF NOT EXISTS revoked_by UUID REFERENCES platform_users(id);

COMMENT ON COLUMN api_key.revoked_by IS
    'The user who revoked this key. NULL while the key is live, or for a key revoked directly in SQL.';


-- ####################################################################
-- ##  versions/v1.4.sql
-- ####################################################################

-- ===========================================================================
-- v1.4 — Detection-worker role, and the SUPER_ADMIN backfill it exposed
-- ===========================================================================
--
-- Model 2's AI worker (ai-worker/, Python) needs an API key that can:
--
--   * discover cameras            GET  /api/v1/vms, /vms/{id}/cameras   -> vms.read
--   * submit detections           POST /api/v1/detections               -> observation.write
--   * report its own liveness     POST /api/v1/worker-health/heartbeat  -> worker.heartbeat
--
-- A key acts through an access group, and a group pairs ONE role with scopes —
-- there is no way to attach a loose set of permissions to a group. So the
-- worker needs a role that holds exactly those three. None of the seeded roles
-- do, hence DETECTION_WORKER below.
--
-- WHY THE SUPER_ADMIN BACKFILL
--
-- v1.sql grants SUPER_ADMIN every permission with a CROSS JOIN, but that ran
-- before v1.2 added observation.write / watchlist.manage / worker.heartbeat and
-- before v1.3 added apikey.*. Those permissions are currently held by NO role,
-- so nobody — not even a super administrator — can reach POST /detections or
-- the worker-health endpoints. Re-running the CROSS JOIN fixes that generally;
-- it is idempotent.
--
-- Requires: v1.sql, v1.1.sql, v1.2.sql, v1.3.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- SUPER_ADMIN backfill — every permission, including those added since v1.sql
-- ---------------------------------------------------------------------------

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- The human side of the detection demo path (search / watchlist / worker view)
-- ---------------------------------------------------------------------------

-- STATE_ADMIN already administers everything else; it needs the read and
-- watchlist-management side so an operator can run the "search metadata ->
-- correlate -> raise alert" steps. Detection INGEST stays out — that is a
-- machine action, and DETECTION_WORKER is the only role that carries it.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN', 'observation.read'),
    ('STATE_ADMIN', 'watchlist.manage'),
    ('STATE_ADMIN', 'alert.read'),
    ('STATE_ADMIN', 'worker.heartbeat')
)
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- DETECTION_WORKER — the machine role for ai-worker/
-- ---------------------------------------------------------------------------

-- is_system: shipped with the platform, not operator-defined, so the UI and the
-- escalation guards treat it like the other built-ins.
INSERT INTO roles (code, name, description, is_system) VALUES
    ('DETECTION_WORKER', 'Detection Worker',
     'Model 2 AI worker: camera discovery, detection ingest and heartbeat. Machine only.', TRUE)
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, description = EXCLUDED.description;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('DETECTION_WORKER', 'vms.read'),
    ('DETECTION_WORKER', 'observation.write'),
    ('DETECTION_WORKER', 'worker.heartbeat')
)
ON CONFLICT DO NOTHING;


-- ####################################################################
-- ##  versions/v1.5.sql
-- ####################################################################

-- ===========================================================================
-- v1.5 — Stream-credential resolution for the AI worker
-- ===========================================================================
--
-- Model 2's AI worker (ai-worker/, Python) connects to camera RTSP streams
-- directly. Those streams are usually authenticated, and the worker had no way
-- to obtain the username/password: the registry never returns credential
-- values, and until now no route on the API did either.
--
-- This version adds ONE deliberately narrow read path:
--
--   GET /api/v1/vms/{id}/credential/resolve  -> credential.resolve
--
-- returning the resolved username/password/token for a connector target the
-- caller can already reach. It is the machine analogue of what the .NET
-- connector runtime already does in-process through ICredentialResolver, and
-- every resolution is written to credential_access_log exactly as that path is.
--
-- WHY THIS IS SAFE TO ADD
--
--   * credential.resolve is a distinct permission, held by exactly one role
--     (DETECTION_WORKER) — a machine role with no interactive login.
--   * Scope still runs through the target: a caller can only resolve a
--     credential for a VMS row their organization/geography grants reach to.
--   * The resolver audits every call, success or failure, so "which worker
--     read which credential, and when" stays answerable.
--   * PUT /vms/{id}/credential and /status are unchanged; the write side still
--     never echoes secret material.
--
-- Requires: v1.sql, v1.1.sql, v1.2.sql, v1.3.sql, v1.4.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Permission
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('credential.resolve', 'Resolve device credentials',
     'admin',
     'Read the resolved username/password/token for a connector target, in order to connect '
     || 'to it. Machine callers only; every resolution is audit-logged.')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN receives every permission (v1.sql's CROSS JOIN only ran against
-- the permissions that existed then). Re-run for the one just added.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- DETECTION_WORKER gains credential.resolve
-- ---------------------------------------------------------------------------

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('DETECTION_WORKER', 'credential.resolve')
)
ON CONFLICT DO NOTHING;


-- ####################################################################
-- ##  versions/v1.6.sql
-- ####################################################################

-- ===========================================================================
-- v1.6 — Centralised CCTV Registry & GIS Mapping
-- ===========================================================================
--
-- Adds the AUTHORITATIVE camera registry. Until now the only camera rows in the
-- system were federation.federated_camera — an OBSERVATION of what a VMS
-- currently reports, keyed by (target_id, native_camera_id), never
-- authoritative, camera_id always NULL because the registry did not exist.
--
-- This version adds the registry itself:
--
--   cameras                 one row per registered CCTV asset. Ownership
--                           (organization_unit_id) and location (site_id) are
--                           independent dimensions — CAMERA-SCHEMA.md's core
--                           rule — and the hierarchy is resolved through the
--                           referenced rows, not duplicated here.
--   camera_health_history   health-status transitions (manual overrides now;
--                           a bus-driven writer later). Not partitioned: unlike
--                           the per-poll camera_status_history, this table
--                           records transitions only and stays small.
--   maintenance_records     maintenance lifecycle per camera. A record moving
--                           to IN_PROGRESS / COMPLETED is what drives the
--                           camera's maintenance_status.
--
-- GEOMETRY DECISION
--
--   No PostGIS, no geometry column. Consistent with CLAUDE.md ("No PostgreSQL
--   extensions") and with federated_camera / sites. Coverage sectors are pure
--   trigonometry over DECIMAL lat/long, computed in the application on read.
--   Coverage-GAP analysis (ST_Union / ST_Difference over boundary polygons) is
--   deferred to the version that introduces spatial querying.
--
-- RECONCILIATION
--
--   federated_camera.camera_id (nullable, no FK) is the link. It is set by
--   POST /api/v1/cameras/{id}/reconcile and never cleared by the inventory
--   poll (the connector inventory merge omits it from ON CONFLICT DO UPDATE).
--   No federation-side schema change is needed here.
--
-- Requires: v1.sql, v1.1.sql, v1.2.sql, v1.3.sql, v1.4.sql, v1.5.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- cameras — the authoritative registry record
-- ---------------------------------------------------------------------------
--
-- Units: azimuth / tilt / *_fov in degrees; effective_range / *_height /
-- altitude in metres. Enumerations are VARCHAR + CHECK (matches sites,
-- geographic_areas, CAMERA-SCHEMA.md) rather than new ENUM types.

CREATE TABLE IF NOT EXISTS cameras (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    camera_code           VARCHAR(100) NOT NULL,
    name                  VARCHAR(255) NOT NULL,

    -- The two independent dimensions. Both required: a registered camera always
    -- has an owner and a place. (federated_camera.site_id is nullable because a
    -- VMS may report a camera before it is sited; the registry does not.)
    organization_unit_id  UUID NOT NULL REFERENCES organization_units(id),
    site_id               UUID NOT NULL REFERENCES sites(id),

    -- No vendors table yet (vendor_kind is a federation-side enum, not an entity).
    -- Vendor is free text for now; a vendor entity is a later change.
    manufacturer          VARCHAR(255),
    model                 VARCHAR(255),
    camera_type           VARCHAR(50) NOT NULL
                          CHECK (camera_type IN
                              ('FIXED','PTZ','DOME','BULLET','ANPR','THERMAL','MULTISENSOR','OTHER')),
    serial_number         VARCHAR(255),

    latitude              DECIMAL(10,7) NOT NULL CHECK (latitude  BETWEEN -90  AND 90),
    longitude             DECIMAL(10,7) NOT NULL CHECK (longitude BETWEEN -180 AND 180),
    altitude              DECIMAL(10,3) CHECK (altitude BETWEEN -500 AND 9000),
    mounting_height       DECIMAL(10,3) CHECK (mounting_height BETWEEN 0 AND 200),

    azimuth               DECIMAL(7,3) CHECK (azimuth        >= 0   AND azimuth        <  360),
    tilt                  DECIMAL(7,3) CHECK (tilt           >= -90 AND tilt           <= 90),
    horizontal_fov        DECIMAL(7,3) CHECK (horizontal_fov >  0   AND horizontal_fov <= 360),
    vertical_fov          DECIMAL(7,3) CHECK (vertical_fov   >  0   AND vertical_fov   <= 180),
    effective_range       DECIMAL(10,3) CHECK (effective_range > 0  AND effective_range <= 5000),

    ip_address            INET,
    port                  INTEGER CHECK (port BETWEEN 1 AND 65535),
    protocol              VARCHAR(50)
                          CHECK (protocol IS NULL OR protocol IN
                              ('RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER')),

    -- The connector_target.id this camera is served by. No FK: a registry camera may name a VMS that
    -- is later removed, mirroring federated_camera.camera_id having no FK back.
    vms_id                UUID,
    stream_reference      VARCHAR(512),
    -- Opaque pointer into the secret layer. The registry never resolves it.
    credential_reference  VARCHAR(255),

    installation_date     DATE,

    operational_status    VARCHAR(30) NOT NULL DEFAULT 'UNKNOWN'
                          CHECK (operational_status IN ('ONLINE','OFFLINE','DEGRADED','UNKNOWN')),
    connectivity_status   VARCHAR(30) NOT NULL DEFAULT 'UNKNOWN'
                          CHECK (connectivity_status IN ('CONNECTED','DISCONNECTED','UNKNOWN')),
    maintenance_status    VARCHAR(30) NOT NULL DEFAULT 'NORMAL'
                          CHECK (maintenance_status IN
                              ('NORMAL','REQUIRED','UNDER_MAINTENANCE','RETIRED')),

    last_seen_at          TIMESTAMPTZ,
    last_health_check_at  TIMESTAMPTZ,

    -- Soft delete / retire. The row stays for the sake of historical
    -- observations and reconciliation links.
    deleted_at            TIMESTAMPTZ,
    deleted_by            UUID,

    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by            UUID NOT NULL,
    updated_by            UUID NOT NULL,

    -- RETIRED and the retirement stamp travel together, both directions.
    CONSTRAINT ck_cameras_retire_consistent CHECK (
        (maintenance_status = 'RETIRED') = (deleted_at IS NOT NULL)
    )
);

-- camera_code is unique among LIVE rows only: retiring a camera frees its code
-- for reuse, and a partial unique index is how that is expressed.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cameras_code_live
    ON cameras (camera_code) WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_cameras_organization_unit ON cameras (organization_unit_id);
CREATE INDEX IF NOT EXISTS ix_cameras_site              ON cameras (site_id);
CREATE INDEX IF NOT EXISTS ix_cameras_camera_type       ON cameras (camera_type);
CREATE INDEX IF NOT EXISTS ix_cameras_operational_status ON cameras (operational_status);
CREATE INDEX IF NOT EXISTS ix_cameras_maintenance_status ON cameras (maintenance_status);
CREATE INDEX IF NOT EXISTS ix_cameras_vms               ON cameras (vms_id) WHERE vms_id IS NOT NULL;
-- Bounding-box scans for the GIS feed, and keyset list ordering, over live rows.
CREATE INDEX IF NOT EXISTS ix_cameras_bbox_live         ON cameras (latitude, longitude)
    WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------------------
-- camera_health_history — status transitions
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS camera_health_history (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    camera_id           UUID NOT NULL REFERENCES cameras(id) ON DELETE CASCADE,

    operational_status  VARCHAR(30) NOT NULL
                        CHECK (operational_status IN ('ONLINE','OFFLINE','DEGRADED','UNKNOWN')),
    connectivity_status VARCHAR(30) NOT NULL
                        CHECK (connectivity_status IN ('CONNECTED','DISCONNECTED','UNKNOWN')),

    checked_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    latency_ms          INTEGER CHECK (latency_ms >= 0),
    error_code          VARCHAR(100),
    failure_reason      TEXT,
    source              VARCHAR(20) NOT NULL DEFAULT 'MANUAL'
                        CHECK (source IN ('MANUAL','FEDERATION','AI_WORKER','PROBE')),
    details             JSONB NOT NULL DEFAULT '{}'::jsonb,
    recorded_by         UUID
);

CREATE INDEX IF NOT EXISTS ix_camera_health_history_camera
    ON camera_health_history (camera_id, checked_at DESC);

-- ---------------------------------------------------------------------------
-- maintenance_records — maintenance lifecycle
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS maintenance_records (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    camera_id         UUID NOT NULL REFERENCES cameras(id) ON DELETE CASCADE,

    maintenance_type  VARCHAR(40) NOT NULL
                      CHECK (maintenance_type IN
                          ('PREVENTIVE','CORRECTIVE','INSPECTION','INSTALLATION',
                           'RELOCATION','DECOMMISSION','OTHER')),
    status            VARCHAR(20) NOT NULL DEFAULT 'OPEN'
                      CHECK (status IN ('OPEN','IN_PROGRESS','COMPLETED','CANCELLED')),

    description       TEXT NOT NULL,
    failure_reason    TEXT,

    reported_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at        TIMESTAMPTZ,
    completed_at      TIMESTAMPTZ,
    next_due_at       TIMESTAMPTZ,

    -- Free text: maintenance is often done by an external contractor, not a
    -- platform user.
    performed_by      VARCHAR(255),

    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by        UUID NOT NULL,
    updated_by        UUID NOT NULL,

    CONSTRAINT ck_maintenance_records_completed_at CHECK (
        (status = 'COMPLETED') = (completed_at IS NOT NULL)
    ),
    CONSTRAINT ck_maintenance_records_time_order CHECK (
        started_at IS NULL OR started_at >= reported_at
    )
);

CREATE INDEX IF NOT EXISTS ix_maintenance_records_camera
    ON maintenance_records (camera_id, reported_at DESC);
CREATE INDEX IF NOT EXISTS ix_maintenance_records_open
    ON maintenance_records (camera_id) WHERE status IN ('OPEN','IN_PROGRESS');
CREATE INDEX IF NOT EXISTS ix_maintenance_records_due
    ON maintenance_records (next_due_at)
    WHERE status <> 'CANCELLED' AND next_due_at IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Permissions
-- ---------------------------------------------------------------------------
--
-- camera.read / camera.create / camera.update / camera.health.read /
-- camera.maintenance.* / gis.read / gis.coverage.read already exist (v1.sql).
-- These three are new: retire, bulk import and reconciliation are each distinct
-- enough from "edit a camera" to be delegated or withheld on their own.

INSERT INTO permissions (code, name, category, description) VALUES
    ('camera.delete',    'Retire cameras',      'camera', 'Soft-delete / retire camera registry entries'),
    ('camera.import',    'Bulk import cameras', 'camera', 'Create cameras in bulk from an import batch'),
    ('camera.reconcile', 'Reconcile cameras',   'camera', 'Link registry cameras to VMS-discovered cameras')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN gets every permission, including these. The CROSS JOIN only runs
-- when its own file runs (v1.4 exists because this was once missed), so v1.6
-- must repeat it.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r CROSS JOIN permissions p WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    -- New registry admin actions.
    ('STATE_ADMIN','camera.delete'), ('STATE_ADMIN','camera.import'), ('STATE_ADMIN','camera.reconcile'),
    ('DEPARTMENT_ADMIN','camera.delete'), ('DEPARTMENT_ADMIN','camera.import'),
    ('DEPARTMENT_ADMIN','camera.reconcile'),

    -- VMS_ADMIN is the reconciliation user; from-federated also needs camera.create.
    ('VMS_ADMIN','camera.reconcile'), ('VMS_ADMIN','camera.create'),

    -- v1.sql gave maintenance to MAINTENANCE_OPERATOR only, and camera.health.read
    -- to everyone except STATE_ADMIN. Close both gaps so the admin roles can use
    -- the health and maintenance endpoints.
    ('STATE_ADMIN','camera.health.read'),
    ('STATE_ADMIN','camera.maintenance.read'), ('STATE_ADMIN','camera.maintenance.update'),
    ('DEPARTMENT_ADMIN','camera.maintenance.read'), ('DEPARTMENT_ADMIN','camera.maintenance.update')
)
ON CONFLICT DO NOTHING;


-- ####################################################################
-- ##  versions/v1.7.sql
-- ####################################################################

-- ===========================================================================
-- v1.7 — vms.delete permission
-- ===========================================================================
--
-- DELETE /api/v1/vms/{id} was gated on vms.update: the same permission that
-- lets someone edit a poll interval also let them permanently destroy a
-- target and its whole discovered inventory, capability matrix and health
-- history. That is the same smell camera.delete (v1.6) fixed for the camera
-- registry, applied here to the VMS registry. See docs/API-REVIEW-FINDINGS.md
-- Finding 9-H3.
--
-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

INSERT INTO permissions (code, name, category, description) VALUES
    ('vms.delete', 'Delete VMS integrations', 'vms',
     'Permanently remove a connector target and its discovered inventory')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN gets every permission, including this one. The CROSS JOIN only
-- runs when its own file runs, so every version that adds a permission must
-- repeat it (v1.4, v1.5, v1.6 all do).
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r CROSS JOIN permissions p WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- The same roles that hold vms.update today get vms.delete: deleting a target
-- is a natural extension of the same administrative authority that edits or
-- quarantines it, not a separately delegated action. Unlike camera.delete,
-- where VMS_ADMIN was deliberately excluded (it is the reconciliation user,
-- not the registry admin), VMS_ADMIN here IS the target's own admin — "the
-- Model 3 administrator" per its v1.sql description — so it is included.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN', 'vms.delete'),
    ('DEPARTMENT_ADMIN', 'vms.delete'),
    ('VMS_ADMIN', 'vms.delete')
)
ON CONFLICT DO NOTHING;


-- ####################################################################
-- ##  versions/v1.8.sql
-- ####################################################################

-- ===========================================================================
-- v1.8 — refresh tokens + platform_users.token_version (PR4)
-- ===========================================================================
--
-- Until now a login returned one stateless 8h bearer token: deactivating a
-- user, resetting their password, or removing them from a group did nothing
-- to a token already issued, and there was no way to sign a user out at all.
--
-- PR4 splits that into:
--   * a short (~15 min) access token, carrying the `trinetra:tokenver` claim;
--   * an opaque 8h SLIDING refresh token, hash stored here, presented to
--     POST /auth/refresh to rotate the pair.
--
-- token_version is bumped on logout / password change / admin reset /
-- deactivation / group removal; OnTokenValidated rejects a token whose claim
-- no longer matches the stored value. Refresh-token rows are revoked in the
-- same transaction as each of those events. See docs/API-REVIEW-FINDINGS.md
-- findings 4-C1 / 4-M2.
--
-- No new permissions: POST /auth/refresh is anonymous (it authenticates by
-- presenting the refresh secret), POST /auth/logout is any-authenticated.
--
-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

-- Monotonic per-user counter. Every access token carries the value it was
-- minted against. DEFAULT 0 so existing rows are valid and any pre-v1.8
-- token keeps working until it expires on its own.
ALTER TABLE platform_users
    ADD COLUMN IF NOT EXISTS token_version INTEGER NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS refresh_token (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    user_id       UUID NOT NULL REFERENCES platform_users(id) ON DELETE CASCADE,

    -- SHA-256 hex of a 256-bit CSPRNG value, generated exactly as api_key's
    -- raw value is. The raw value exists only in the login / refresh response
    -- body; a database dump must not yield working sessions.
    token_hash    TEXT NOT NULL UNIQUE,

    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- now() + refresh lifetime at issue. Each rotation issues a fresh row with
    -- a fresh expiry, so the window slides forward on use and lapses on idle.
    expires_at    TIMESTAMPTZ NOT NULL,

    -- Set when this token is rotated. A non-null used_at on a presented token
    -- is a replay signal (AuthEndpoints.RefreshAsync).
    used_at       TIMESTAMPTZ,

    -- The successor row minted when this one was rotated. A forensic chain only
    -- — deliberately NOT a foreign key: a nightly sweep deletes expired rows
    -- oldest-first, and a self-FK would either block that or force ON DELETE
    -- side-effects on rows we are about to delete anyway.
    replaced_by   UUID,

    -- Bulk-set by logout / password change / admin reset / deactivation /
    -- group removal / reuse detection.
    revoked_at    TIMESTAMPTZ
);

-- Bulk revoke by user, and "the live tokens for this user" — the hot path for
-- every revocation event.
CREATE INDEX IF NOT EXISTS ix_refresh_token_user ON refresh_token (user_id);

-- A nightly cleanup sweep (systemd timer, follow-up not in PR4) deletes rows
-- past expires_at. Rows are harmless until then — every state check is
-- timestamp-based.
CREATE INDEX IF NOT EXISTS ix_refresh_token_expires ON refresh_token (expires_at);


-- ####################################################################
-- ##  versions/v1.9.sql
-- ####################################################################

-- ===========================================================================
-- v1.9 — auth_audit: the authentication event trail (PR7b, finding 4-H3)
-- ===========================================================================
--
-- config_audit records who changed what. It says nothing about who logged in,
-- who failed to, whose account locked, when a token was rotated or replayed,
-- or which API key authenticated from where. For an estate holding camera
-- credentials and citizen-surveillance data that is a baseline accountability
-- gap, and it is also how the brute-force activity finding 4-H5 makes possible
-- gets detected.
--
-- auth_audit is append-only, partitioned by month like config_audit, read only
-- through the new authaudit.read permission (SUPER_ADMIN only — deliberately
-- NOT bundled with config.audit.read), and retained on its own period
-- (Retention:AuthAuditMonths, default 24) separate from AuditMonths.
--
-- The login failure RESPONSE is coarse — an unknown username and a wrong
-- password for a real account both return the same 401. The TRAIL records which:
-- a resolved user_id means the name existed, a presented_username with no
-- user_id means it did not. That is deliberate — an incident investigator needs
-- the distinction, and the trail is readable only by SUPER_ADMIN, so it is not
-- an enumeration surface. API-key auth SUCCESS is sampled
-- (~one row per key per 5 minutes, paired with the coarse last_used_at in 7c);
-- API-key auth FAILURE is always recorded.
--
-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

CREATE TABLE IF NOT EXISTS auth_audit (
    id                 BIGINT GENERATED ALWAYS AS IDENTITY,
    occurred_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    event_type         TEXT NOT NULL,   -- AuthAuditEvents.* in application code
    outcome            TEXT NOT NULL
                       CHECK (outcome IN ('success', 'failure', 'lockout', 'revoked')),

    -- ON DELETE SET NULL on both: a user or key delete must never be blocked by,
    -- or cascade into, the audit trail.
    user_id            UUID REFERENCES platform_users(id) ON DELETE SET NULL,
    api_key_id         UUID REFERENCES api_key(id) ON DELETE SET NULL,

    -- The typed-in string, only when the principal could not be resolved. A
    -- resolved user_id makes it redundant and possibly misleading.
    presented_username TEXT
                       CHECK (presented_username IS NULL OR char_length(presented_username) <= 256),
    CONSTRAINT auth_audit_username_only_when_anon
        CHECK (presented_username IS NULL OR user_id IS NULL),

    source_address     TEXT,   -- Connection.RemoteIpAddress as-is; forwarded-header
                               -- / trusted-proxy hardening is a separate work item.
    user_agent         TEXT,
    jti                TEXT,   -- refresh-token id, where the event has one
    detail             JSONB,

    PRIMARY KEY (occurred_at, id)
) PARTITION BY RANGE (occurred_at);

CREATE TABLE IF NOT EXISTS auth_audit_default PARTITION OF auth_audit DEFAULT;

CREATE INDEX IF NOT EXISTS ix_auth_audit_user
    ON auth_audit (user_id, occurred_at DESC);

-- The triage query: everything that is not a clean success.
CREATE INDEX IF NOT EXISTS ix_auth_audit_failures
    ON auth_audit (occurred_at DESC) WHERE outcome <> 'success';


-- ---------------------------------------------------------------------------
-- Partition maintenance: auth_audit joins the monthly audit tables.
-- ---------------------------------------------------------------------------
-- Body-only change (one extra array element); signature unchanged, so
-- CREATE OR REPLACE is correct.

CREATE OR REPLACE FUNCTION ensure_audit_partitions(
    from_month DATE DEFAULT date_trunc('month', CURRENT_DATE)::date,
    months_ahead INTEGER DEFAULT 3
) RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    m         DATE;
    created   INTEGER := 0;
    tbl       TEXT;
    part_name TEXT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY['credential_access_log', 'config_audit', 'auth_audit'] LOOP
        FOR i IN 0..months_ahead LOOP
            m := (date_trunc('month', from_month) + (i || ' month')::interval)::date;
            part_name := format('%s_%s', tbl, to_char(m, 'YYYYMM'));

            IF NOT EXISTS (
                SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relname = part_name AND n.nspname = 'federation'
            ) THEN
                EXECUTE format(
                    'CREATE TABLE federation.%I PARTITION OF federation.%I '
                    'FOR VALUES FROM (%L) TO (%L)',
                    part_name, tbl, m, (m + interval '1 month')::date);
                created := created + 1;
            END IF;
        END LOOP;
    END LOOP;

    RETURN created;
END $$;


-- auth_audit has its OWN retention period, so it does not match
-- drop_audit_partitions_before's '^(config_audit|credential_access_log)_...' — it
-- gets a sibling driven by Retention:AuthAuditMonths.

CREATE OR REPLACE FUNCTION drop_auth_audit_partitions_before(cutoff DATE)
RETURNS TABLE (dropped_partition TEXT)
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    r RECORD;
BEGIN
    IF cutoff IS NULL OR cutoff > CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing retention cutoff % : it is not in the past.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    FOR r IN
        WITH monthly AS MATERIALIZED (
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation'
              AND c.relname ~ '^auth_audit_[0-9]{6}$'
        )
        SELECT relname FROM monthly
        WHERE (to_date(right(relname, 6), 'YYYYMM') + interval '1 month')::date <= cutoff
    LOOP
        EXECUTE format('DROP TABLE federation.%I', r.relname);
        dropped_partition := r.relname;
        RETURN NEXT;
    END LOOP;
END $$;


-- retention_status gains an auth_audit row. No column/type change.

CREATE OR REPLACE VIEW retention_status AS
WITH parts AS (
    SELECT c.relname,
           CASE
               WHEN c.relname ~ '^federation_event_[0-9]{8}$'   THEN 'federation_event'
               WHEN c.relname ~ '^connector_health_[0-9]{8}$'   THEN 'connector_health'
               WHEN c.relname ~ '^config_audit_[0-9]{6}$'       THEN 'config_audit'
               WHEN c.relname ~ '^credential_access_log_[0-9]{6}$' THEN 'credential_access_log'
               WHEN c.relname ~ '^auth_audit_[0-9]{6}$'         THEN 'auth_audit'
           END AS table_name,
           CASE
               WHEN c.relname ~ '[0-9]{8}$' THEN to_date(right(c.relname, 8), 'YYYYMMDD')
               ELSE to_date(right(c.relname, 6), 'YYYYMM')
           END AS covers_from,
           pg_total_relation_size(c.oid) AS bytes
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'federation'
      AND (c.relname ~ '^federation_event_[0-9]{8}$'
        OR c.relname ~ '^connector_health_[0-9]{8}$'
        OR c.relname ~ '^config_audit_[0-9]{6}$'
        OR c.relname ~ '^credential_access_log_[0-9]{6}$'
        OR c.relname ~ '^auth_audit_[0-9]{6}$')
)
SELECT table_name,
       count(*)              AS partitions,
       min(covers_from)      AS oldest_data,
       max(covers_from)      AS newest_partition,
       pg_size_pretty(sum(bytes)) AS total_size
FROM parts
GROUP BY table_name;


-- ---------------------------------------------------------------------------
-- Permission: read the authentication audit trail. SUPER_ADMIN only.
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('authaudit.read', 'View authentication audit trail', 'admin',
     'Read the append-only record of logins, lockouts, token rotation, password '
     || 'changes and API-key authentication outcomes')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category,
        description = EXCLUDED.description;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, 'authaudit.read'
FROM roles r
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;


-- ####################################################################
-- ##  versions/v1.10.sql
-- ####################################################################

-- ===========================================================================
-- v1.10 — password_history (PR7e, finding 4-M5)
-- ===========================================================================
--
-- Two related controls on password reuse:
--
--   * password_history keeps the last N (N = 5, enforced in application code)
--     PBKDF2 parameter sets per user. On every successful password set — self
--     change or admin reset — the OUTGOING hash is appended here and rows
--     beyond the newest N are pruned. A new password is rejected if it verifies
--     against any retained row.
--
--   * A self-service change is refused if the current password is younger than
--     24h (PasswordPolicy.MinimumAge, application code), which blocks cycling
--     through N changes in a minute to reach an old password. Admin reset is
--     EXEMPT from the age check (an admin unlocking a stuck user must not be
--     blocked) but still writes and honours history.
--
-- "Current password set-at" is derived: it is the set_at of the newest
-- password_history row for the user (that row holds the password this one
-- replaced, stamped when the replacement happened). A user who has never
-- changed their password has no history row and is not age-limited.
--
-- A silent rehash-on-login (cost upgrade) writes NO history row — the password
-- did not change (UserRepository.RehashPasswordAsync).
--
-- Breached-password screening (finding 4-M6) is deliberately NOT here — it is
-- its own later PR, it needs a bundled offline wordlist.
--
-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

CREATE TABLE IF NOT EXISTS password_history (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    user_id              UUID NOT NULL
                         REFERENCES platform_users(id) ON DELETE CASCADE,

    -- Same shape as the four password_* columns on platform_users. PBKDF2
    -- params are per row so an old entry still verifies at its own cost.
    password_hash        BYTEA   NOT NULL,
    password_salt        BYTEA   NOT NULL,
    password_iterations  INTEGER NOT NULL,
    password_algorithm   TEXT    NOT NULL,

    set_at               TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- The only access pattern: "newest N rows for this user", for the reuse check,
-- the prune, and (newest row alone) the minimum-age check.
CREATE INDEX IF NOT EXISTS ix_password_history_user
    ON password_history (user_id, set_at DESC);


-- ####################################################################
-- ##  versions/v1.11.sql
-- ####################################################################

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


-- ####################################################################
-- ##  versions/v1.12.sql
-- ####################################################################

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


-- ####################################################################
-- ##  versions/v1.13.sql
-- ####################################################################

-- ===========================================================================
-- v1.13 — AI-worker heartbeat hardening (API review Finding 17: M1, M2, M3, L1, L2)
-- ===========================================================================
--
-- The worker-health heartbeat (POST /api/v1/worker-health/heartbeat) trusted a
-- client-supplied worker id and a client-supplied timestamp, took no caller
-- context, and shared one permission between "submit a heartbeat" and "read the
-- whole fleet inventory". For a monitoring signal that is too loose:
--
--   17-M1 — anyone holding worker.heartbeat could POST as any worker id and keep
--           a dead process looking alive, or flood fake ids to bury a real
--           outage. Ownership is now bound to the API key that reports:
--           ai_worker_health.api_key_id is NOT NULL and part of the identity, so
--           a heartbeat can only ever touch a row under the caller's own key.
--
--   17-M2 — last_heartbeat_at was set from the client clock; a future-dated
--           value made a worker look alive forever. It is now written
--           server-side (application passes now via the repository); the
--           client's claimed time is kept only as reported_at, for drift
--           diagnosis (CLAUDE.md #6 — clocks drift, so keep both, trust ours).
--
--   17-M3 — one permission gated both routes. Split into worker.read (see the
--           fleet) and worker.heartbeat (submit only). STATE_ADMIN loses
--           worker.heartbeat (a human admin submits no heartbeats — v1.4
--           copy-paste) and gains worker.read. New worker.manage gates the
--           retire route below.
--
--   17-L1 — no way to clear a decommissioned worker: after 17-M3 a human admin
--           can see a stale row but not remove it, and a fleet resize
--           (WORKER_COUNT 4 -> 2) leaves ai-worker-2-of-4 / -3-of-4 stale
--           forever, read by every consumer as a crash. New
--           DELETE /api/v1/worker-health/{id} (worker.manage), audited.
--
--   17-L2 — over-long / bad-charset worker id or hostname 500'd (raw 22001).
--           Validated in the endpoint now; the column caps below are the
--           backstop.
--
-- FRESH DATA. ai_worker_health is dropped and recreated rather than altered:
-- the API is deployed nowhere, the heartbeat push is still a no-op (it only
-- runs once ai-worker's BACKEND_HEARTBEAT_URL is set), the rows are ephemeral
-- observability with no audit or business meaning and every worker re-registers
-- within one interval, and no foreign key references the table. An ALTER adding
-- a NOT NULL owner column to existing rows would need a fabricated key id
-- anyway.
--
-- Requires: v1.sql .. v1.12.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- ai_worker_health — rebuilt with an owning key in the identity
-- ---------------------------------------------------------------------------

DROP TABLE IF EXISTS ai_worker_health;

CREATE TABLE ai_worker_health (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The API key that reported this heartbeat. Part of the worker's identity,
    -- not just metadata: a heartbeat upsert matches on (api_key_id, worker_id,
    -- hostname), so one integration can never write or overwrite another's
    -- liveness. ON DELETE CASCADE purges a deleted key's rows; a *revoked* key
    -- keeps revoked_at rather than being deleted, so its rows linger until the
    -- retire route (17-L1) or a future pruning job (17-L4) clears them.
    api_key_id          UUID NOT NULL REFERENCES api_key(id) ON DELETE CASCADE,

    -- The worker's own label for itself (ai-worker's WORKER_INDEX-of-COUNT).
    -- Honest client value, scoped to the reporting key's namespace; ownership
    -- is carried by api_key_id, never by parsing this string.
    worker_id           VARCHAR(128) NOT NULL,

    -- Included in the identity so two hosts accidentally running the same
    -- WORKER_INDEX under one key show as two rows (the useful signal) instead
    -- of silently refreshing one merged row.
    hostname            VARCHAR(253) NOT NULL,

    first_seen_at       TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Server-authoritative. The application always writes now() here; a client
    -- timestamp never reaches this column.
    last_heartbeat_at   TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- The client's claimed send time. Advisory only — never used in staleness
    -- logic, kept so (last_heartbeat_at - reported_at) exposes clock drift.
    reported_at         TIMESTAMPTZ,

    -- Leading column api_key_id also serves the FK cascade-delete lookup and any
    -- "WHERE api_key_id = ..." — no separate single-column index needed.
    CONSTRAINT uq_ai_worker_identity UNIQUE (api_key_id, worker_id, hostname)
);

COMMENT ON TABLE ai_worker_health IS
    'Liveness registry for Model 2 AI workers (POST /api/v1/worker-health/heartbeat). '
    'Identity is (api_key_id, worker_id, hostname): a heartbeat is bound to the reporting key '
    '(Finding 17-M1). last_heartbeat_at is server-set; reported_at is the advisory client clock '
    '(17-M2). Not worker_node — an AI worker owns a static camera partition, not a lease.';

-- ---------------------------------------------------------------------------
-- Permissions — split the one worker.heartbeat permission
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('worker.read',   'View AI-worker fleet', 'vms',
        'List AI-worker processes and their last heartbeat'),
    ('worker.manage', 'Retire AI-worker records', 'vms',
        'Delete a stale or decommissioned AI-worker health record')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN holds every permission, including any added since v1.sql — the
-- CROSS JOIN there ran before these existed. Unconditional, idempotent, the
-- same backfill v1.4 / v1.11 / v1.12 use.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- The human admin roles that watch the fleet. Explicit list, not "every role
-- that holds worker.heartbeat" — that set includes DETECTION_WORKER, and the
-- machine worker reading the fleet inventory is exactly the target list for a
-- 17-M1 spoofing attack. STATE_ADMIN and DEPARTMENT_ADMIN administer workers;
-- only STATE_ADMIN retires records.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN',      'worker.read'),
    ('STATE_ADMIN',      'worker.manage'),
    ('DEPARTMENT_ADMIN', 'worker.read')
)
ON CONFLICT DO NOTHING;

-- STATE_ADMIN loses worker.heartbeat (v1.4 gave it by copy-paste alongside the
-- read side of the detection demo path). A human admin submits no heartbeats;
-- DETECTION_WORKER keeps it and stays the only role that carries it. A plain
-- grant delete — no customized_at guard, that concern is only for a future
-- re-seed of a preset's whole composition.
DELETE FROM role_permissions rp
USING roles r
WHERE rp.role_id = r.id
  AND r.code = 'STATE_ADMIN'
  AND rp.permission_code = 'worker.heartbeat';


-- ####################################################################
-- ##  versions/v1.14.sql
-- ####################################################################

-- ===========================================================================
-- v1.14 — email format + uniqueness (API review Finding 4-M5 / 6-M5)
-- ===========================================================================
--
-- platform_users.email was a plain nullable VARCHAR(255) — no format check, no
-- uniqueness. Two accounts could share an email (or a typo'd one), and there
-- was no constraint to catch it. Both F2 (forgot password) and F3 (SSO account
-- linking) need email to actually identify one account, so this closes the gap
-- ahead of either landing, not as part of them.
--
-- Format validation stays application-side (CreateAsync/UpdateAsync) — a
-- CHECK constraint duplicating that regex in SQL would drift from it over
-- time; the constraint here is for what SQL is actually good at: uniqueness.
--
-- Case-insensitive (lower(email)) and partial (WHERE email IS NOT NULL): most
-- mail providers treat the local part as case-sensitive in principle but
-- effectively case-insensitive in practice, and every account created before
-- this version has email = NULL, which must never collide with itself.

CREATE UNIQUE INDEX IF NOT EXISTS ux_platform_users_email
    ON platform_users (lower(email))
    WHERE email IS NOT NULL;


-- ####################################################################
-- ##  versions/v1.15.sql
-- ####################################################################

-- ===========================================================================
-- v1.15 — inventory freshness on connector_target
-- ===========================================================================
--
-- The inventory loop (~daily, GetCamerasAsync) and the status loop (~30s,
-- GetCameraStatusAsync) in Federation.Runtime.TargetWorker both wrote through
-- the same ConnectorStateStore.UpsertCamerasAsync, so there was no way to tell
-- "when did we last fully re-enumerate this target's inventory" apart from
-- "when did the fast status loop last touch any camera". The frontend just
-- shipped a comparison between expected_camera_count and the discovered count
-- with a manual link to reconciliation, but nothing tells an operator when
-- that count was last actually refreshed, or lets them force a refresh.
--
-- Why these are new columns rather than reusing what already exists:
--   - federated_camera.updated_at is per-camera and touched by BOTH loops (the
--     status loop's UpsertCamerasAsync writes it too), so it cannot answer
--     "when was inventory last enumerated" for the target as a whole — a
--     target whose status loop is healthy but whose inventory loop has been
--     silently failing would still show a recent updated_at on every camera.
--   - connector_health.camera_count is a health-loop snapshot of
--     _lastCameraCount (whichever loop, inventory or status, last set it in
--     memory) at the health tick's 60s cadence — it is an observability
--     sample, not the authoritative "last full inventory sync" record, and it
--     lives in a table partitioned/retained for history, not designed to be
--     read as current target state on every VMS list/get call.
--
-- last_inventory_poll_at / last_inventory_camera_count are written only by
-- the inventory loop (Federation.Storage.ConnectorStateStore.UpsertInventoryAsync),
-- in the same transaction as the camera merge. inventory_poll_requested_at is
-- an operator-facing flag: POST /api/v1/vms/{id}/poll-inventory sets it to
-- now() only (no adapter call from the API process, per CLAUDE.md's per-VMS
-- polling non-negotiable); TargetWorker's health loop compares it against its
-- own in-memory last-completed-inventory-poll time and brings the next
-- inventory pass forward through the existing CallAsync/resilience wrapper
-- when it is newer.

SET search_path = federation, public;

ALTER TABLE connector_target
    ADD COLUMN IF NOT EXISTS last_inventory_poll_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS last_inventory_camera_count INTEGER,
    ADD COLUMN IF NOT EXISTS inventory_poll_requested_at TIMESTAMPTZ;

COMMENT ON COLUMN connector_target.last_inventory_poll_at IS
    'When the inventory loop (GetCamerasAsync, ~daily cadence) last completed '
    'against this target. Distinct from federated_camera.updated_at, which the '
    'status loop (~30s) also touches, and from connector_health.camera_count, '
    'which is a health-loop observability sample rather than the authoritative '
    'record of the last full re-enumeration. Written only by '
    'ConnectorStateStore.UpsertInventoryAsync, never by the shared '
    'UpsertCamerasAsync the status loop uses.';

COMMENT ON COLUMN connector_target.last_inventory_camera_count IS
    'Camera count returned by the most recent completed inventory poll — the '
    'count to compare against expected_camera_count for silent inventory '
    'drift, at the freshness last_inventory_poll_at records. Distinct from '
    'connector_health.camera_count, which reflects whichever loop (inventory '
    'or status) last reported a count as of the last 60s health tick.';

COMMENT ON COLUMN connector_target.inventory_poll_requested_at IS
    'Operator request to bring the next inventory pass forward, set by '
    'POST /api/v1/vms/{id}/poll-inventory. The API process never calls the '
    'adapter itself; it only sets this timestamp. TargetWorker''s health loop '
    'checks it and, when newer than the worker''s own last completed inventory '
    'poll, runs an extra inventory pass through the normal CallAsync '
    'rate-limit/circuit-breaker wrapper — never bypassing it.';


-- ####################################################################
-- ##  versions/v1.16.sql
-- ####################################################################

-- ===========================================================================
-- v1.16 — camera event-recording flag + standalone reachability probe
-- ===========================================================================
--
-- Two independent additions, both scoped to the standalone camera registry
-- (cameras), not Model 3 federation targets:
--
--   1. cameras.record_events — a plain operator-set flag controlling whether
--      this camera's future event stream should be persisted once Model 2/3
--      start writing events for registry cameras. Not derived from anything;
--      defaults true so existing and newly registered cameras keep their
--      current (implicit) behaviour unless explicitly turned off.
--
--   2. camera_connection_test — a job table for a pre-save reachability probe
--      (POST /api/v1/cameras/connection-test), mirroring connection_test's
--      async create-then-poll shape (v1.sql) but for a camera that does not
--      exist as a row yet, so it cannot carry a camera_id FK. No credential,
--      no vendor adapter: the probe (Federation.Runtime) opens a plain TCP
--      socket to ip_address:port with a short timeout and reports whether it
--      opened — nothing about the device beyond that. Federation.Adapters is
--      not involved, matching CLAUDE.md's "adapters are the only place
--      vendor-specific code lives": this deliberately has none.
--
--      Retention reuses the existing `ConnectionTests` cutoff
--      (RetentionOptions) — same category of ephemeral job row, not worth a
--      second knob for.
--
-- Requires: v1.sql .. v1.15.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- 1. cameras.record_events
-- ---------------------------------------------------------------------------

ALTER TABLE cameras
    ADD COLUMN IF NOT EXISTS record_events BOOLEAN NOT NULL DEFAULT true;

COMMENT ON COLUMN cameras.record_events IS
    'Operator-set flag: whether this camera''s future event stream should be '
    'persisted. Plain column, not derived from anything else on the row. '
    'Defaults true so registering a camera keeps the current implicit '
    'behaviour unless the operator turns it off.';

-- ---------------------------------------------------------------------------
-- 2. camera_connection_test — pre-save reachability probe for standalone cameras
-- ---------------------------------------------------------------------------
--
-- No camera_id: the whole point is to test host:port before a camera row
-- exists to attach it to. requested_by/executed_by follow connection_test's
-- shape (TEXT actor label, not a FK) for the same reason — a test can outlive
-- the session that started it.

CREATE TABLE IF NOT EXISTS camera_connection_test (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    ip_address      INET NOT NULL,
    port            INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
    protocol        VARCHAR(50) NOT NULL
                    CHECK (protocol IN
                        ('RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER')),

    status          VARCHAR(20) NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending','running','completed','failed','abandoned')),

    requested_by    TEXT NOT NULL,
    requested_at    TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Which instance picked the job up. Needed to recognise work abandoned by a dead instance.
    executed_by     TEXT,
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,

    result          JSONB,
    failure_reason  TEXT
);

-- The sweeper's access path: jobs still claimed to be running.
CREATE INDEX IF NOT EXISTS ix_camera_connection_test_active
    ON camera_connection_test (started_at)
    WHERE status IN ('pending', 'running');

CREATE INDEX IF NOT EXISTS ix_camera_connection_test_requested
    ON camera_connection_test (requested_at DESC);

-- Marks jobs abandoned when the executing instance died mid-test. Same shape as
-- sweep_abandoned_connection_tests (v1.sql), one table over.
CREATE OR REPLACE FUNCTION sweep_abandoned_camera_connection_tests(
    stale_after INTERVAL DEFAULT '5 minutes')
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    swept INTEGER;
BEGIN
    UPDATE camera_connection_test
    SET status = 'abandoned',
        completed_at = now(),
        failure_reason = format(
            'No result after %s. The API instance running this test most likely stopped.',
            stale_after)
    WHERE status IN ('pending', 'running')
      AND COALESCE(started_at, requested_at) < now() - stale_after;

    GET DIAGNOSTICS swept = ROW_COUNT;
    RETURN swept;
END $$;

-- Low-volume table, same reasoning as purge_connection_tests_before: DELETE is
-- fine, no partitioning needed.
CREATE OR REPLACE FUNCTION purge_camera_connection_tests_before(cutoff TIMESTAMPTZ)
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    removed INTEGER;
BEGIN
    DELETE FROM camera_connection_test
    WHERE requested_at < cutoff
      AND status IN ('completed', 'failed', 'abandoned');

    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END $$;


-- ####################################################################
-- ##  versions/v1.17.sql
-- ####################################################################

-- ===========================================================================
-- v1.17 — authenticated credential test for an already-registered camera
-- ===========================================================================
--
-- The registration flow changed: "Test connection" on the register-camera
-- page now creates the camera immediately (POST /api/v1/cameras) with the
-- minimal fields collected so far, saves the operator-entered username and
-- password through the existing PUT /cameras/{id}/credential mechanism, and
-- only then runs a REAL authenticated probe against the device. The rest of
-- the registration form becomes an edit of that camera. This version adds the
-- job table the new probe writes to.
--
-- camera_credential_test is deliberately its own table, not an extension of
-- camera_connection_test (v1.16): that table has no camera_id (it exists to
-- test host:port *before* a camera row exists) and its result shape
-- (CameraReachabilityReport) is TCP-only. This one always names an existing
-- camera, resolves and uses its stored credential, and its result shape
-- carries an authentication outcome, not just reachability.
--
-- No vendor adapter is involved (Federation.Adapters is Model 3 / VMS-only).
-- The prober (Federation.Runtime CameraCredentialProbe) is protocol-aware in
-- a narrow, honest way: HTTP/HTTPS/ONVIF gets a real HTTP Basic-auth request;
-- RTSP/RTSPS gets an RFC 2326 OPTIONS handshake with Basic/Digest; RTMP/SRT/
-- OTHER degrade to a reachability-only result with an explicit
-- "not verifiable" outcome rather than ever claiming a credential was
-- checked when it was not.
--
-- Requires: v1.sql .. v1.16.sql
-- ===========================================================================

SET search_path = federation, public;

CREATE TABLE IF NOT EXISTS camera_credential_test (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    camera_id           UUID NOT NULL REFERENCES cameras(id) ON DELETE CASCADE,

    -- Snapshot of what was tested, taken at claim time. The camera row can be
    -- edited after the test is claimed but before the job runs; the job must
    -- test what the operator saw when they clicked Test, not whatever the
    -- row has drifted to by the time a background task picks it up.
    protocol            VARCHAR(50) NOT NULL
                        CHECK (protocol IN
                            ('RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER')),
    ip_address           INET NOT NULL,
    port                 INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
    credential_reference TEXT NOT NULL,

    status              VARCHAR(20) NOT NULL DEFAULT 'pending'
                        CHECK (status IN ('pending','running','completed','failed','abandoned')),

    requested_by        TEXT NOT NULL,
    requested_at         TIMESTAMPTZ NOT NULL DEFAULT now(),

    executed_by          TEXT,
    started_at           TIMESTAMPTZ,
    completed_at          TIMESTAMPTZ,

    -- CameraCredentialTestReport (Trinetra.Federation.Runtime): reachable,
    -- authOutcome ("authenticated" | "credential_rejected" | "not_verifiable"
    -- | "unreachable" | "error"), connectMs, detail, failure.
    result               JSONB,
    failure_reason        TEXT
);

CREATE INDEX IF NOT EXISTS ix_camera_credential_test_camera
    ON camera_credential_test (camera_id, requested_at DESC);

-- The sweeper's access path: jobs still claimed to be running.
CREATE INDEX IF NOT EXISTS ix_camera_credential_test_active
    ON camera_credential_test (started_at)
    WHERE status IN ('pending', 'running');

CREATE INDEX IF NOT EXISTS ix_camera_credential_test_requested
    ON camera_credential_test (requested_at DESC);

-- Same shape as sweep_abandoned_camera_connection_tests (v1.16), one table over.
CREATE OR REPLACE FUNCTION sweep_abandoned_camera_credential_tests(
    stale_after INTERVAL DEFAULT '5 minutes')
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    swept INTEGER;
BEGIN
    UPDATE camera_credential_test
    SET status = 'abandoned',
        completed_at = now(),
        failure_reason = format(
            'No result after %s. The API instance running this test most likely stopped.',
            stale_after)
    WHERE status IN ('pending', 'running')
      AND COALESCE(started_at, requested_at) < now() - stale_after;

    GET DIAGNOSTICS swept = ROW_COUNT;
    RETURN swept;
END $$;

-- Low-volume table, same reasoning as purge_camera_connection_tests_before:
-- DELETE is fine, no partitioning needed. Shares the ConnectionTests
-- retention cutoff (RetentionOptions) -- same category of ephemeral job row.
CREATE OR REPLACE FUNCTION purge_camera_credential_tests_before(cutoff TIMESTAMPTZ)
RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    removed INTEGER;
BEGIN
    DELETE FROM camera_credential_test
    WHERE requested_at < cutoff
      AND status IN ('completed', 'failed', 'abandoned');

    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END $$;


-- ####################################################################
-- ##  versions/v1.18.sql
-- ####################################################################

-- ===========================================================================
-- v1.18 — Correlation: windowed cross-camera matching (ARCHITECTURE-MODEL-3.md §8, Gap 1)
-- ===========================================================================
--
-- Day-one correlation is windowed SQL polling over federation_event, behind the
-- ICorrelationEngine port (SqlCorrelationEngine in Trinetra.Federation.Storage) --
-- NOT Kafka. correlation_rule holds the per-rule config (time window, spatial
-- radius, confidence floor, required signal agreement) so operators can tune
-- without a deployment; it is system-seeded here and has no write API in this
-- pass. correlation_group is the cluster a rule found; correlation_group_member
-- is its join table, since a group can have more than two members.
--
-- Idempotency (CLAUDE.md #5): the unique constraint is on
-- (rule_id, natural_key, window_bucket), not on the group's own id, so
-- SqlCorrelationEngine re-running an overlapping window on restart hits
-- ON CONFLICT DO NOTHING rather than creating a duplicate group.
--
-- correlation_group_member denormalises organization_unit_id / geographic_area_id
-- (and camera_id / source_vms_id) from federation_event onto every member row --
-- the same reasoning federation_event itself documents: correlation must not
-- join back to a partitioned, 100-400M-rows/day table to resolve scope. It
-- also means the API's scoped read (CorrelationRepository) never touches
-- federation_event at all. federation_event_id is stored rather than an FK:
-- federation_event's primary key is (occurred_at, event_id) because it is
-- RANGE partitioned on occurred_at, so a real FK would need the partition key
-- carried here too for no benefit -- event_occurred_at is kept alongside it
-- for that same reason, as a plain column, not a constraint.
--
-- New permission correlation.read, granted like worker.read in v1.13: SUPER_ADMIN
-- (via the unconditional CROSS JOIN backfill), STATE_ADMIN, DEPARTMENT_ADMIN.
-- No write endpoint and no permission for one in this pass.
--
-- Requires: v1.sql .. v1.17.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- correlation_rule — configuration, not code (architecture §8)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS correlation_rule (
    id                         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    code                       TEXT NOT NULL UNIQUE,
    name                       TEXT NOT NULL,
    description                TEXT,

    -- Events sharing an object_reference are bucketed into windows of this width;
    -- only events in the same bucket can join one group.
    time_window_seconds        INTEGER NOT NULL CHECK (time_window_seconds > 0),

    -- Plain-trig haversine radius against the bucket's centroid -- no PostGIS
    -- (CLAUDE.md: no PostgreSQL extensions). NULL disables the spatial check.
    spatial_radius_meters      DOUBLE PRECISION CHECK (spatial_radius_meters IS NULL OR spatial_radius_meters > 0),

    -- Minimum average member confidence for a candidate group to be recorded.
    confidence_floor           DOUBLE PRECISION NOT NULL
                                CHECK (confidence_floor >= 0 AND confidence_floor <= 1),

    -- Minimum number of distinct cameras that must agree before a group forms.
    -- A single camera's own repeated observations never form a group alone.
    required_signal_agreement  INTEGER NOT NULL DEFAULT 2 CHECK (required_signal_agreement >= 2),

    is_active                  BOOLEAN NOT NULL DEFAULT TRUE,
    created_at                 TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at                 TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE correlation_rule IS
    'Per-rule correlation config: time window, spatial radius, confidence floor, required '
    'signal agreement (architecture §8). System-seeded; no write API in this pass -- edited by '
    'a future admin route, not by a deployment.';

-- ---------------------------------------------------------------------------
-- correlation_group — one cluster a rule found
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS correlation_group (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rule_id           UUID NOT NULL REFERENCES correlation_rule(id),

    -- The federation_event.object_reference the group formed on: a plate, a
    -- person reference, a track id.
    natural_key       TEXT NOT NULL,

    -- Start of the time bucket this group was computed for.
    window_bucket     TIMESTAMPTZ NOT NULL,

    -- Possible-match score in [0,1]. NEVER certainty -- cross-camera identity is
    -- probabilistic (CLAUDE.md production posture); every surface presenting
    -- this must say "possible match", never "confirmed".
    confidence        DOUBLE PRECISION NOT NULL CHECK (confidence >= 0 AND confidence <= 1),

    member_count      INTEGER NOT NULL CHECK (member_count >= 2),
    first_occurred_at TIMESTAMPTZ NOT NULL,
    last_occurred_at  TIMESTAMPTZ NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Idempotency key (CLAUDE.md #5): NOT on the group's own id, so a restart or
    -- an overlapping run of SqlCorrelationEngine over the same window hits
    -- ON CONFLICT DO NOTHING instead of creating a duplicate group.
    CONSTRAINT uq_correlation_group_dedup UNIQUE (rule_id, natural_key, window_bucket)
);

COMMENT ON TABLE correlation_group IS
    'One cluster of events an ICorrelationEngine run judged a possible match. confidence is '
    'always a possible-match score, never certainty. Deduplicated on '
    '(rule_id, natural_key, window_bucket), not on id.';

-- The API's list access path: recent groups, newest activity first.
CREATE INDEX IF NOT EXISTS ix_correlation_group_last_occurred
    ON correlation_group (last_occurred_at DESC);

-- ---------------------------------------------------------------------------
-- correlation_group_member — join table; a group can have more than 2 members
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS correlation_group_member (
    group_id             UUID NOT NULL REFERENCES correlation_group(id) ON DELETE CASCADE,

    -- federation_event.event_id -- not an FK. federation_event is RANGE
    -- partitioned on occurred_at and its primary key is (occurred_at, event_id),
    -- so a real FK would need the partition key carried here too for no
    -- benefit; event_occurred_at below is kept as a plain column for that
    -- reason, alongside the scope columns every event query already denormalises.
    federation_event_id  TEXT NOT NULL,
    event_occurred_at    TIMESTAMPTZ NOT NULL,

    camera_id             TEXT NOT NULL,
    source_vms_id          UUID NOT NULL,

    -- Denormalised from federation_event so the API's scoped read
    -- (CorrelationRepository) never joins back to the 100-400M-rows/day event
    -- table -- the same reasoning federation_event itself documents for why it
    -- denormalises organization_unit_id off the camera.
    organization_unit_id UUID NOT NULL,
    geographic_area_id   UUID,

    PRIMARY KEY (group_id, federation_event_id)
);

COMMENT ON TABLE correlation_group_member IS
    'Join table: the federation_event rows that make up one correlation_group. '
    'organization_unit_id / geographic_area_id are denormalised from federation_event so a '
    'scoped read never joins back to the partitioned event table.';

-- The scoped-read access path: "does this group have a member this caller can reach".
CREATE INDEX IF NOT EXISTS ix_correlation_member_org
    ON correlation_group_member (group_id, organization_unit_id);
CREATE INDEX IF NOT EXISTS ix_correlation_member_geo
    ON correlation_group_member (group_id, geographic_area_id)
    WHERE geographic_area_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Permission — same shape as worker.read (v1.13)
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('correlation.read', 'View correlation groups', 'events',
        'List cross-camera possible-match groups and their member events')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN holds every permission -- unconditional, idempotent backfill, the
-- same one v1.4 / v1.11 / v1.12 / v1.13 use.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN',      'correlation.read'),
    ('DEPARTMENT_ADMIN', 'correlation.read')
)
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- Seed rule — the ANPR cross-camera scenario the demo path needs
-- ---------------------------------------------------------------------------

INSERT INTO correlation_rule
    (code, name, description, time_window_seconds, spatial_radius_meters,
     confidence_floor, required_signal_agreement, is_active)
VALUES
    ('ANPR_CROSS_CAMERA', 'ANPR cross-camera match',
     'Same plate seen by two or more cameras within a 10-minute window. No spatial radius -- '
     'plate movement between camera sites is exactly the signal this rule looks for.',
     600, NULL, 0.6, 2, TRUE)
ON CONFLICT (code) DO NOTHING;


-- ####################################################################
-- ##  versions/v1.19.sql
-- ####################################################################

-- ===========================================================================
-- v1.19 — surveyed boundary polygons on geographic_areas (PostGIS)
-- ===========================================================================
--
-- CLAUDE.md's "no PostgreSQL extensions" invariant is deliberately reversed here, by explicit
-- project-owner decision — see the note added to CLAUDE.md alongside this file. Coverage sectors
-- (v1.6) stay pure C# in Federation.Core; PostGIS is introduced only for what plain lat/long
-- cannot do: measuring a camera's coverage against a real administrative boundary.
--
-- boundary is nullable — most areas will not have surveyed polygon data on day one, and a camera
-- registry, GIS point feed and coverage-sector rendering must all keep working with zero rows
-- populated. Only the new coverage-gap analysis (GET /gis/gaps) requires it, and that route
-- reports "no boundary set" rather than guessing when it is absent (never silently "fully
-- covered" or "fully uncovered" — CLAUDE.md's production-posture rule against presenting an
-- unknown as a fact).
--
-- The boundary write path is an admin-only bulk import
-- (POST /api/v1/geographic-areas/bulk-import-boundaries), matching the existing camera
-- bulk-import shape, not a per-area form field — this is surveyed reference data, not something
-- an operator free-hands.
--
-- Deployment note: the bare-metal Postgres host needs the PostGIS extension package installed
-- (e.g. postgresql-<version>-postgis-3) before this file is applied, and CREATE EXTENSION needs
-- superuser or a pre-granted role — see docs/DEPLOYMENT.md. This file does not run itself; per
-- the Dockerfile and every other version file, nothing in the running system touches the schema.

CREATE EXTENSION IF NOT EXISTS postgis;

ALTER TABLE geographic_areas ADD COLUMN IF NOT EXISTS boundary geometry(Polygon, 4326);

CREATE INDEX IF NOT EXISTS ix_geographic_areas_boundary
    ON geographic_areas USING GIST (boundary);


-- ####################################################################
-- ##  versions/v1.20.sql
-- ####################################################################

-- ===========================================================================
-- v1.20 — per-user video-wall layout preference
-- ===========================================================================
--
-- The video wall's tile layout (how many tiles, which camera in each) is a personal UI
-- preference, not department-scoped or RBAC-sensitive domain data — one row per user, keyed to
-- their own id, read/written only through their own session. It carries no organization or
-- geography scope, no permission gate beyond authentication, and no audit row (CLAUDE.md's
-- "every sensitive operation is... audit-logged" invariant is aimed at department-scoped domain
-- mutations and privileged administration, not a personal layout choice).
--
-- camera_ids is stored as an array rather than a join table: the tile order matters (it is the
-- grid position) and the array's own ordinal position carries it, with no need for a separate
-- position column or the up-front normalization a join table would need for what is, at 80,000
-- cameras, still a tiny per-user list. The wall has no fixed tile count: a caller adds/removes
-- cameras one at a time and rows grow or shrink to fit; tile_count is kept in sync with
-- camera_ids' length purely as a cheap read-side convenience (avoids every reader recomputing
-- array_length), not a separate source of truth — the API always writes it as cameraIds.length.
--
-- No FK to cameras(id): a camera can be deleted or move out of the caller's scope after being
-- placed on the wall, and the layout should not be silently rewritten or blocked by that — the
-- API resolves ids against the caller's current camera access at read time and leaves a
-- now-invisible id in place rather than pruning it, the same "let it go stale, don't hide state"
-- choice used elsewhere in this schema.

CREATE TABLE IF NOT EXISTS video_wall_preference (
    user_id      UUID PRIMARY KEY REFERENCES platform_users(id) ON DELETE CASCADE,
    tile_count   INTEGER NOT NULL CHECK (tile_count BETWEEN 1 AND 64),
    column_count INTEGER NOT NULL DEFAULT 2 CHECK (column_count BETWEEN 1 AND 12),
    camera_ids   UUID[] NOT NULL DEFAULT '{}'::uuid[],
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);


-- ####################################################################
-- ##  versions/v1.21.sql
-- ####################################################################

-- ===========================================================================
-- v1.21 — descriptive HR metadata on platform_users (org unit, geographic area, designation)
-- ===========================================================================
--
-- Three columns for "who this person is" in the org chart, distinct from "what this account can
-- do", which comes entirely from access-group membership (role + org/geo scopes). Rationale:
-- access groups say what a user may reach; these three say which department they sit in, which
-- area they cover, and their job title, for display and reporting only.
--
-- organization_unit_id / geographic_area_id are DESCRIPTIVE ONLY, the same pattern already used
-- for organization_units.geographic_area_id (v1.11, invariant 12): validated for existence +
-- ACTIVE on write, and NEVER read by has_permission, authorized_org_units,
-- authorized_geographic_areas, unscoped_permissions, or any other scope-resolution function or
-- predicate. Organization and geography remain independent scope dimensions, ANDed, sourced only
-- from access_groups/scopes — these columns carry zero authorization weight and must not be
-- joined into CallerContext, a scope predicate, or any *_read/*_manage query's WHERE clause.
--
-- designation is free text (job title), capped to the column width by the API, no other
-- validation — it is a label, not a controlled vocabulary.
--
-- All three are nullable: most existing accounts will not have this set, and it is never
-- required to create or use an account.

ALTER TABLE platform_users
    ADD COLUMN organization_unit_id UUID REFERENCES organization_units(id),
    ADD COLUMN geographic_area_id   UUID REFERENCES geographic_areas(id),
    ADD COLUMN designation          VARCHAR(150);




-- ####################################################################
-- ##  versions/v1.22.sql
-- ####################################################################

-- ===========================================================================
-- v1.22 — camera credential resolution for the streaming gateway (G1)
-- ===========================================================================
--
-- docs/STREAMING-GATEWAY-PLAN.md's G1: the live-streaming gateway (browser-facing, opens RTSP
-- connections to cameras directly, same reason ai-worker needed v1.5's
-- GET /api/v1/vms/{id}/credential/resolve) needs the symmetric route for a STANDALONE registry
-- camera's own credential_reference:
--
--   GET /api/v1/cameras/{id}/credential/resolve  -> camera.credential.resolve
--
-- WHY A NEW PERMISSION AND A NEW ROLE, NOT REUSE OF v1.5's DETECTION_WORKER
--
--   * DETECTION_WORKER holds credential.resolve (VMS-target credentials) AND observation.write.
--     The streaming gateway is browser/operator-facing — a materially larger attack surface than
--     ai-worker's backend-only VMS polling — so a compromise there must not also be able to
--     forge ANPR/person/vehicle observations into the metadata layer that correlation, search
--     and alerting all trust. Two roles can each independently hold the SAME permission code
--     without coupling their other grants — STREAMING_GATEWAY holding credential.resolve does
--     not give it DETECTION_WORKER's observation.write, since permissions are independent grants
--     on a role, not a shared role. A distinct role is what actually keeps them apart; a distinct
--     permission code is not required for that, and camera.credential.resolve (standalone-camera
--     credentials) is still its own new permission because it gates a genuinely new route, not
--     because credential.resolve needed to be avoided.
--   * STREAMING_GATEWAY holds: camera.read (CameraRepository.GetAsync's own scope requirement),
--     camera.credential.resolve (the new route above, for a standalone camera's own
--     credential_reference), vms.read (ConnectorTargetRepository.GetAsync's equivalent
--     requirement), and credential.resolve (the EXISTING GET /vms/{id}/credential/resolve's
--     explicit .RequirePermission gate — vms.read alone satisfies only the repository's internal
--     scope check, not the route itself, which requires credential.resolve independently). The
--     vms.read + credential.resolve pair lets the gateway resolve a VMS-managed camera's
--     credential from its connector target when the camera's own credential_reference is null
--     (the ordinary case for such a camera — Federation.Core's Camera model never falls back
--     automatically; the caller resolves whichever row actually holds the reference). Still no
--     observation.write, no worker.heartbeat, nothing else DETECTION_WORKER has.
--
-- Requires: v1.sql .. v1.21.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Permission
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('camera.credential.resolve', 'Resolve standalone camera credentials',
     'admin',
     'Read the resolved username/password/token stored directly on a standalone registry '
     || 'camera, in order to connect to its stream. Machine callers only; every resolution is '
     || 'audit-logged. Distinct from credential.resolve (VMS-target credentials) so the two '
     || 'disclosure paths can be held by different, narrower machine roles.')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN receives every permission (v1.sql's CROSS JOIN only ran against the permissions
-- that existed then). Re-run for the one just added.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------------
-- STREAMING_GATEWAY — the machine role for the live-streaming gateway
-- ---------------------------------------------------------------------------

-- is_system: shipped with the platform, not operator-defined, so the UI and the escalation
-- guards treat it like the other built-ins. status is set ACTIVE explicitly: the column default
-- became DRAFT in v1.12 (which grants nothing), and presets are seeded ACTIVE.
INSERT INTO roles (code, name, description, is_system, status) VALUES
    ('STREAMING_GATEWAY', 'Streaming Gateway',
     'Live-streaming gateway (docs/STREAMING-GATEWAY-PLAN.md): resolves one camera''s credential '
     || 'to open its RTSP stream. Deliberately narrower than DETECTION_WORKER — no '
     || 'observation.write, no worker.heartbeat. Machine only.',
     TRUE, 'ACTIVE')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, description = EXCLUDED.description
    WHERE roles.customized_at IS NULL;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STREAMING_GATEWAY', 'camera.read'),
    ('STREAMING_GATEWAY', 'vms.read'),
    ('STREAMING_GATEWAY', 'camera.credential.resolve'),
    ('STREAMING_GATEWAY', 'credential.resolve')
)
ON CONFLICT DO NOTHING;




-- ####################################################################
-- ##  versions/v1.23.sql
-- ####################################################################

-- ===========================================================================
-- v1.23 — reusable saved credentials for camera registration
-- ===========================================================================
--
-- Registering many cameras at one site today means re-typing the same device username/password
-- into every camera's own sealed credential (CameraCredentialEndpoints). This adds a small named
-- library an operator can populate once ("Site NVR admin", "Axis default") and then point several
-- cameras at, instead of retyping.
--
-- Deliberately NOT a new secret store: `saved_credential` holds only metadata (name, description,
-- which reference). The secret itself is sealed exactly as it is everywhere else in this schema —
-- one row in the existing `secret` table, written through the existing `SecretWriter`, under a
-- reference of the form `saved-credential:{id}`.
--
-- Chosen as a SHARED reference, not copy-on-select: a camera's own `credentialReference` (already
-- a plain, operator-settable field on `POST/PUT /cameras` — see v1.sql's `cameras` table and
-- `CameraContracts.CameraWriteRequest`) is set to the SAME reference string as the library entry.
-- Cameras sharing a library entry therefore share one sealed secret; rotating the library entry's
-- password rotates it for every camera pointed at it, and resolution (`GET
-- /cameras/{id}/credential/resolve`, the streaming gateway, ai-worker) needs no new code path —
-- it already resolves whatever `credential_reference` a camera carries. This is an explicit
-- project-owner choice over a copy-on-select model: the tradeoff is that deleting or rotating a
-- library entry affects every camera using it, which is the point.
--
-- No new permission: gated the same way CameraCredentialEndpoints already is (see that class's
-- remarks) — `camera.read` to list (metadata only, never secret material) and `camera.update` to
-- create, matching the audience that already manages the registry rather than requiring
-- `credential.write` ("Highest privilege in the system") and stranding them.
--
-- Requires: v1.sql .. v1.22.sql
-- ===========================================================================

SET search_path = federation, public;

CREATE TABLE IF NOT EXISTS saved_credential (
    id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Operator-facing label ("Site NVR admin") and optional free-text note. Unique on name so the
    -- picker in the camera form has no ambiguous duplicates to choose between.
    name                 VARCHAR(255) NOT NULL,
    description          TEXT,

    -- Points into the existing `secret` table (v1.sql). FK'd, not just a bare string: a saved
    -- credential with no sealed secret behind it would be a silent dead entry in the picker.
    credential_reference TEXT NOT NULL UNIQUE REFERENCES secret(credential_reference),

    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by           TEXT NOT NULL,

    CONSTRAINT uq_saved_credential_name UNIQUE (name)
);




-- ####################################################################
-- ##  versions/v1.24.sql
-- ####################################################################

-- ===========================================================================
-- v1.24 — rotate/edit/delete for the saved-credential library, and usage tracking
-- ===========================================================================
--
-- v1.23 shipped the saved-credential library as create + list only, deliberately (see that
-- file's remarks) — the missing piece was rotation, and rotation is the entire point of the
-- SHARED-reference design it chose: a camera points at a saved credential's `credential_reference`
-- directly rather than getting its own copy, specifically so that rotating the saved credential
-- rotates it for every camera using it. Without a rotate route, "shared" was only shared at
-- creation time.
--
-- This adds:
--   * `updated_at` / `updated_by` on `saved_credential`, so a rotation is visible the same way
--     every other editable row in this schema records its last edit.
--   * Nothing else schema-side — "how many/which cameras use this reference" is answered by
--     querying `cameras.credential_reference` directly (already an unconstrained TEXT column,
--     v1.6), not by a new join table. A saved credential and the cameras pointed at its reference
--     are linked only by both holding the same string, exactly as designed in v1.23; adding a
--     foreign key from `cameras` to `saved_credential` would wrongly forbid a camera from using a
--     hand-entered `credential_reference` that was never created through the library.
--
-- Requires: v1.sql .. v1.23.sql
-- ===========================================================================

SET search_path = federation, public;

ALTER TABLE saved_credential
    ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_by TEXT;




-- ####################################################################
-- ##  versions/v1.25.sql
-- ####################################################################

-- ===========================================================================
-- v1.25 — native HLS/WebRTC stream sources, bypassing MediaMTX's RTSP remux
-- ===========================================================================
--
-- The streaming gateway (docs/STREAMING-GATEWAY-PLAN.md) has so far assumed every camera is
-- RTSP-only and needs MediaMTX to pull it and remux to HLS. Some cameras/NVRs expose their own
-- native HLS and/or WebRTC (WHEP) endpoints directly — for those, pulling their RTSP feed into
-- MediaMTX just to re-serve HLS is unnecessary work the device has already done itself. This
-- lets a camera opt into pointing the gateway at its own native URL instead, per-protocol:
--
--   RTSP    (default) — unchanged: MediaMTX pulls stream_reference, Federation.Api proxies its
--            HLS output, exactly as today. The only mode every existing camera has.
--   HLS     — Federation.Api proxies native_hls_url directly; MediaMTX is never involved for
--             this camera. For dashboards, mobile, restricted networks.
--   WEBRTC  — Federation.Api proxies only the WHEP *signaling* exchange (the SDP offer/answer
--             POST, and the session-teardown DELETE) to native_webrtc_url; the actual media
--             then flows directly between the viewer's browser and the device over WebRTC,
--             never through this API. For low-latency browser preview.
--
-- RTSP itself stays available unconditionally regardless of this preference — an AI-inference
-- consumer (OpenCV/GStreamer/FFmpeg/DeepStream) connects to stream_reference directly and never
-- goes through this gateway or cares what a browser viewer's preference is.
--
-- Requires: v1.sql .. v1.24.sql
-- ===========================================================================

SET search_path = federation, public;

ALTER TABLE cameras
    ADD COLUMN IF NOT EXISTS stream_preference VARCHAR(20) NOT NULL DEFAULT 'RTSP'
        CHECK (stream_preference IN ('RTSP', 'HLS', 'WEBRTC')),
    ADD COLUMN IF NOT EXISTS native_hls_url TEXT,
    ADD COLUMN IF NOT EXISTS native_webrtc_url TEXT;




-- ####################################################################
-- ##  versions/v1.26.sql
-- ####################################################################

-- ===========================================================================
-- v1.26 — operator/free-form tagging on detections
-- ===========================================================================
--
-- RFP Model 2 "event tagging": `detection_event.event_type` is deliberately a closed set
-- (ANPR_DETECTED | VEHICLE_DETECTED — finding 15-L2, a free-text classification search and
-- dashboards can't reason about). That closed set is the AI worker's own machine classification
-- and stays exactly as-is; this is a separate, additive concept — free-form labels a human
-- operator attaches during review/triage ("suspicious", "false positive", "priority-follow-up",
-- anything they want), independent of what the detection was machine-classified as.
--
-- A normal table, not a partition of detection_event: tag volume is a small fraction of
-- detection volume (most detections are never tagged), and every read/write here is by a human
-- in a UI, not a high-throughput ingest path — none of the reasons detection_event itself is
-- partitioned by time apply to its tags.
--
-- No FK to detection_event: its primary key is (occurred_at, event_id) — occurred_at is the
-- partition key, so a normal FK "just referencing event_id" isn't expressible, and requiring
-- every tag write to carry occurred_at just to satisfy a constraint reintroduces exactly the
-- coupling denormalisation avoids elsewhere on this row (see detection_event's own
-- organization_unit_id/geographic_area_id comments). Existence + scope are instead checked by
-- the endpoint reading detection_event directly before writing a tag (DetectionTagEndpoints).
--
-- organization_unit_id/geographic_area_id are denormalised onto the tag row at write time, the
-- same convention detection_event itself already uses ("scoping a query must not join to resolve
-- ownership") — a tag list/delete never has to re-join detection_event to enforce scope.
--
-- Requires: v1.sql .. v1.25.sql
-- ===========================================================================

SET search_path = federation, public;

CREATE TABLE IF NOT EXISTS detection_tag (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    detection_event_id    TEXT NOT NULL,
    detection_occurred_at TIMESTAMPTZ NOT NULL,

    tag                   TEXT NOT NULL CHECK (char_length(tag) BETWEEN 1 AND 40),

    -- Denormalised from detection_event at write time — see the note above.
    organization_unit_id  UUID NOT NULL REFERENCES organization_units(id),
    geographic_area_id    UUID,

    created_by            UUID REFERENCES platform_users(id),
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Case-insensitive dedup: "Reviewed" and "reviewed" are the same tag. ON CONFLICT targets this
-- expression index directly (DetectionTagRepository.AddTagAsync).
CREATE UNIQUE INDEX IF NOT EXISTS ux_detection_tag_dedup
    ON detection_tag (detection_event_id, lower(tag));

CREATE INDEX IF NOT EXISTS ix_detection_tag_event
    ON detection_tag (detection_event_id);
CREATE INDEX IF NOT EXISTS ix_detection_tag_org
    ON detection_tag (organization_unit_id);


COMMIT;
