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
