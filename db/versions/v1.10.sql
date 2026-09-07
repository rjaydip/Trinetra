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
