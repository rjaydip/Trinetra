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

ALTER TABLE federation.platform_users
    ADD COLUMN organization_unit_id UUID REFERENCES federation.organization_units(id),
    ADD COLUMN geographic_area_id   UUID REFERENCES federation.geographic_areas(id),
    ADD COLUMN designation          VARCHAR(150);
