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
-- project-owner choice over a copy-on-select model (see docs/API-REVIEW... no, this decision was
-- made directly, not filed as a review finding): the tradeoff is that deleting or rotating a
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
