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
--
-- A version file is never edited once applied; this is new, not a correction
-- to v1.sql's platform_users definition.

SET search_path = federation, public;

CREATE UNIQUE INDEX IF NOT EXISTS ux_platform_users_email
    ON platform_users (lower(email))
    WHERE email IS NOT NULL;
