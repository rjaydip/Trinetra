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
-- ===========================================================================

-- Plain SET, not SET LOCAL (a no-op outside a transaction block, which is how a migration run
-- with plain psql would silently create geographic_areas.boundary's index in public instead of
-- federation, per every other version file's identical guard). CREATE EXTENSION itself installs
-- PostGIS's own functions/types into whichever schema is first on this search_path, so this line
-- also decides where ST_* becomes resolvable — federation, matching every other object here.
SET search_path = federation, public;

CREATE EXTENSION IF NOT EXISTS postgis;

ALTER TABLE geographic_areas ADD COLUMN IF NOT EXISTS boundary geometry(Polygon, 4326);

CREATE INDEX IF NOT EXISTS ix_geographic_areas_boundary
    ON geographic_areas USING GIST (boundary);
