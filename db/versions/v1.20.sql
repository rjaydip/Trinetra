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
-- grid position, flowing left-to-right/top-to-bottom across column_count columns) and the
-- array's own ordinal position carries it, with no need for a separate position column or the
-- up-front normalization a join table would need for what is, at 80,000 cameras, still a tiny
-- per-user list. The wall has no fixed tile count: a caller adds/removes cameras one at a time
-- and rows grow or shrink to fit; tile_count is kept in sync with camera_ids' length purely as a
-- cheap read-side convenience (avoids every reader recomputing array_length), not a separate
-- source of truth — the API always writes it as cameraIds.length.
--
-- No FK to cameras(id): a camera can be deleted or move out of the caller's scope after being
-- placed on the wall, and the layout should not be silently rewritten or blocked by that — the
-- API resolves ids against the caller's current camera access at read time and leaves a
-- now-invisible id in place rather than pruning it, the same "let it go stale, don't hide state"
-- choice used elsewhere in this schema.

-- Plain SET, not SET LOCAL (a no-op outside a transaction block) — a migration applied with
-- plain psql would otherwise silently create this table in public instead of federation, per
-- every other version file's identical guard.
SET search_path = federation, public;

CREATE TABLE IF NOT EXISTS video_wall_preference (
    user_id      UUID PRIMARY KEY REFERENCES platform_users(id) ON DELETE CASCADE,
    tile_count   INTEGER NOT NULL CHECK (tile_count BETWEEN 1 AND 64),
    column_count INTEGER NOT NULL DEFAULT 2 CHECK (column_count BETWEEN 1 AND 12),
    camera_ids   UUID[] NOT NULL DEFAULT '{}'::uuid[],
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);
