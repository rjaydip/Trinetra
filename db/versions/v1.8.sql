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
