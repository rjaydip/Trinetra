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
