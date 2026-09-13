-- ===========================================================================
-- v1.15 — inventory freshness on connector_target
-- ===========================================================================
--
-- The inventory loop (~daily, GetCamerasAsync) and the status loop (~30s,
-- GetCameraStatusAsync) in Federation.Runtime.TargetWorker both wrote through
-- the same ConnectorStateStore.UpsertCamerasAsync, so there was no way to tell
-- "when did we last fully re-enumerate this target's inventory" apart from
-- "when did the fast status loop last touch any camera". The frontend just
-- shipped a comparison between expected_camera_count and the discovered count
-- with a manual link to reconciliation, but nothing tells an operator when
-- that count was last actually refreshed, or lets them force a refresh.
--
-- Why these are new columns rather than reusing what already exists:
--   - federated_camera.updated_at is per-camera and touched by BOTH loops (the
--     status loop's UpsertCamerasAsync writes it too), so it cannot answer
--     "when was inventory last enumerated" for the target as a whole — a
--     target whose status loop is healthy but whose inventory loop has been
--     silently failing would still show a recent updated_at on every camera.
--   - connector_health.camera_count is a health-loop snapshot of
--     _lastCameraCount (whichever loop, inventory or status, last set it in
--     memory) at the health tick's 60s cadence — it is an observability
--     sample, not the authoritative "last full inventory sync" record, and it
--     lives in a table partitioned/retained for history, not designed to be
--     read as current target state on every VMS list/get call.
--
-- last_inventory_poll_at / last_inventory_camera_count are written only by
-- the inventory loop (Federation.Storage.ConnectorStateStore.UpsertInventoryAsync),
-- in the same transaction as the camera merge. inventory_poll_requested_at is
-- an operator-facing flag: POST /api/v1/vms/{id}/poll-inventory sets it to
-- now() only (no adapter call from the API process, per CLAUDE.md's per-VMS
-- polling non-negotiable); TargetWorker's health loop compares it against its
-- own in-memory last-completed-inventory-poll time and brings the next
-- inventory pass forward through the existing CallAsync/resilience wrapper
-- when it is newer.

SET search_path = federation, public;

ALTER TABLE connector_target
    ADD COLUMN IF NOT EXISTS last_inventory_poll_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS last_inventory_camera_count INTEGER,
    ADD COLUMN IF NOT EXISTS inventory_poll_requested_at TIMESTAMPTZ;

COMMENT ON COLUMN connector_target.last_inventory_poll_at IS
    'When the inventory loop (GetCamerasAsync, ~daily cadence) last completed '
    'against this target. Distinct from federated_camera.updated_at, which the '
    'status loop (~30s) also touches, and from connector_health.camera_count, '
    'which is a health-loop observability sample rather than the authoritative '
    'record of the last full re-enumeration. Written only by '
    'ConnectorStateStore.UpsertInventoryAsync, never by the shared '
    'UpsertCamerasAsync the status loop uses.';

COMMENT ON COLUMN connector_target.last_inventory_camera_count IS
    'Camera count returned by the most recent completed inventory poll — the '
    'count to compare against expected_camera_count for silent inventory '
    'drift, at the freshness last_inventory_poll_at records. Distinct from '
    'connector_health.camera_count, which reflects whichever loop (inventory '
    'or status) last reported a count as of the last 60s health tick.';

COMMENT ON COLUMN connector_target.inventory_poll_requested_at IS
    'Operator request to bring the next inventory pass forward, set by '
    'POST /api/v1/vms/{id}/poll-inventory. The API process never calls the '
    'adapter itself; it only sets this timestamp. TargetWorker''s health loop '
    'checks it and, when newer than the worker''s own last completed inventory '
    'poll, runs an extra inventory pass through the normal CallAsync '
    'rate-limit/circuit-breaker wrapper — never bypassing it.';
