-- v1.28: detection_event accepts a standalone registry camera, not only a VMS-federated one.
--
-- Until now target_id/native_camera_id were NOT NULL, because the only camera a worker could
-- discover was a VMS-federated one (GET /vms/{id}/cameras). v1.27's camera-lease claim endpoint
-- also hands out plain registry cameras (federation.cameras) that have no connector_target at
-- all, so a detection against one of those cameras has no target_id/native_camera_id to store.
--
-- Both columns become nullable; a CHECK enforces that a row always identifies its camera one way
-- or the other. camera_id gets a real FK now — for a registry-sourced detection it is the row's
-- only camera reference, not just an optional post-hoc reconciliation link.

SET search_path = federation, public;

ALTER TABLE detection_event ALTER COLUMN target_id DROP NOT NULL;
ALTER TABLE detection_event ALTER COLUMN native_camera_id DROP NOT NULL;

ALTER TABLE detection_event
    ADD CONSTRAINT ck_detection_event_camera_ref
    CHECK (target_id IS NOT NULL OR camera_id IS NOT NULL);

ALTER TABLE detection_event
    ADD CONSTRAINT fk_detection_event_camera
    FOREIGN KEY (camera_id) REFERENCES cameras(id) ON DELETE CASCADE;

-- ix_detection_event_target (target_id, native_camera_id, occurred_at DESC) already tolerates
-- nulls and stays useful for VMS-sourced rows; add the registry-sourced counterpart.
CREATE INDEX IF NOT EXISTS ix_detection_event_camera_id
    ON detection_event (camera_id, occurred_at DESC)
    WHERE camera_id IS NOT NULL;
