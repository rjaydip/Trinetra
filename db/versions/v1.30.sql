-- v1.30: detection evidence images move from opaque filesystem references into the database.
--
-- Before this, a detection's snapshot_reference was a worker-supplied path — often an absolute
-- path on whatever machine the AI worker itself ran on — that the API could only serve back by
-- treating it as trusted enough to open, defended only by an explicit allow-list of roots
-- (EvidenceStorage.AllowedExternalRoots). That's fragile across anything but a single-machine
-- local setup: a worker on separate edge hardware from the API has no shared filesystem at all.
--
-- detection_snapshot instead holds the image bytes themselves, submitted inline
-- (evidence.snapshotBase64, already an accepted ingest shape — see DetectionEndpoints) and
-- re-encoded server-side at a bounded size/quality before being stored (DetectionEndpoints'
-- evidence pipeline). GET /detections/{id}/evidence now reads this table directly; there is no
-- more filesystem path to validate or trust.
--
-- A separate table, not a column on detection_event, for the same reason detection_tag already
-- is one (see that table's own note): an image blob has no reason to be pulled into memory by a
-- plain detection search that never displays it, and detection_event has no FK target across the
-- partition boundary to attach a foreign key to anyway.

SET search_path = federation, public;

CREATE TABLE IF NOT EXISTS detection_snapshot (
    detection_event_id     TEXT NOT NULL,
    detection_occurred_at  TIMESTAMPTZ NOT NULL,

    image_data              BYTEA NOT NULL,
    content_type            TEXT NOT NULL DEFAULT 'image/jpeg',

    created_at               TIMESTAMPTZ NOT NULL DEFAULT now(),

    PRIMARY KEY (detection_event_id, detection_occurred_at)
);

COMMENT ON TABLE detection_snapshot IS
    'One evidence image per detection, re-encoded server-side to a bounded size before storage '
    '(see DetectionEndpoints). No FK to detection_event — same partitioning constraint '
    'detection_tag already documents.';
