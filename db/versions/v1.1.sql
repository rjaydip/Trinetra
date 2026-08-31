-- ===========================================================================
-- v1.1 — Camera status history
-- ===========================================================================
--
-- Adds a transition log for per-camera status, and the "current status since"
-- stamp that goes with it.
--
-- WHY TRANSITIONS AND NOT SAMPLES
--
-- The status poll runs every 30 seconds per target. At the 80,000 camera design
-- point, one row per camera per poll is 2,667 rows/second — roughly 230M
-- rows/day, the same order as federation_event, for data that changes a few
-- thousand times a day at most. So a row is written only when the status
-- actually changes, and it carries both the previous and the new value.
--
-- The status at any past instant is then the last transition at or before it.
-- What is deliberately NOT recoverable from this table is "was the camera being
-- polled at 10:31?" — that question belongs to connector_health, which records
-- the target's poll cadence, and to federated_camera.last_seen.
--
-- Requires: v1.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- Current status, independent of history retention
-- ---------------------------------------------------------------------------

-- Without this, "Healthy since 3 March" is only answerable while the transition
-- that says so is still inside the retention window. A camera that has been
-- healthy for a year would report no known transition at all, which reads as
-- "never seen" rather than "stable for a year".
ALTER TABLE federated_camera
    ADD COLUMN IF NOT EXISTS status_changed_at TIMESTAMPTZ;

COMMENT ON COLUMN federated_camera.status_changed_at IS
    'When health, is_enabled or is_recording last changed. Survives history retention.';

-- ---------------------------------------------------------------------------
-- Transition log — RANGE partitioned by changed_at
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS camera_status_history (
    target_id           UUID NOT NULL,
    native_camera_id    TEXT NOT NULL,
    changed_at          TIMESTAMPTZ NOT NULL,

    -- Denormalised from federated_camera at write time, for the same reason
    -- federation_event denormalises it: scoping a history query must not join
    -- back to the camera table, and the answer must remain correct even after
    -- the camera row is gone.
    organization_unit_id UUID NOT NULL,
    site_id             UUID,

    -- NULL previous values mean this is the camera's first observed status,
    -- not that the previous value was unknown.
    previous_health     health_status,
    health              health_status NOT NULL,
    previous_enabled    BOOLEAN,
    is_enabled          BOOLEAN NOT NULL,
    previous_recording  BOOLEAN,
    is_recording        BOOLEAN,

    -- The partition key must participate in the primary key on a partitioned
    -- table. A camera cannot transition twice at the same instant: both writes
    -- come from one statement's trigger, in order.
    PRIMARY KEY (changed_at, target_id, native_camera_id)
) PARTITION BY RANGE (changed_at);

-- No foreign key to federated_camera on purpose. A target removed under
-- ON DELETE CASCADE would take its whole status history with it — exactly the
-- record an incident review wants after a site is decommissioned. The columns
-- above are self-sufficient without the parent row.

-- Catches rows whose partition is missing rather than rejecting the insert. The
-- trigger fires inside the camera upsert, so a failure here would take down the
-- inventory write — losing current status to protect history is the wrong way
-- round.
CREATE TABLE IF NOT EXISTS camera_status_history_default
    PARTITION OF camera_status_history DEFAULT;

-- The query this table exists for: one camera's timeline, newest first.
CREATE INDEX IF NOT EXISTS ix_camera_status_history_camera
    ON camera_status_history (target_id, native_camera_id, changed_at DESC);

-- "What broke across this department in the last hour", which is the fleet view
-- rather than the per-camera one.
CREATE INDEX IF NOT EXISTS ix_camera_status_history_org
    ON camera_status_history (organization_unit_id, changed_at DESC);

-- ---------------------------------------------------------------------------
-- Recording the transition
-- ---------------------------------------------------------------------------

-- A trigger rather than application code, because the camera upsert arrives as
-- one binary COPY plus one merge covering a whole target's inventory. Detecting
-- changes in C# would mean reading current state back first — a round trip per
-- poll — and would silently miss any write that did not go through that path
-- (the admin tool, a manual correction in psql).
CREATE OR REPLACE FUNCTION record_camera_status_change() RETURNS TRIGGER
LANGUAGE plpgsql
-- Pinned so the function resolves its own tables regardless of the caller's
-- search_path, matching every other function in this schema.
SET search_path = federation, public
AS $$
BEGIN
    INSERT INTO camera_status_history (
        target_id, native_camera_id, changed_at,
        organization_unit_id, site_id,
        previous_health, health,
        previous_enabled, is_enabled,
        previous_recording, is_recording)
    VALUES (
        NEW.target_id, NEW.native_camera_id, NEW.updated_at,
        NEW.organization_unit_id, NEW.site_id,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.health      END, NEW.health,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.is_enabled  END, NEW.is_enabled,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.is_recording END, NEW.is_recording)
    -- Two writes landing on the same camera at the same instant is a redelivery,
    -- not a second transition. Dropping the duplicate keeps the trigger from
    -- turning an idempotent upsert into a failing one.
    ON CONFLICT DO NOTHING;

    RETURN NULL;
END $$;

-- INSERT and UPDATE are separate triggers, not one AFTER INSERT OR UPDATE.
-- A trigger WHEN clause may only reference OLD and NEW column values: TG_OP is a
-- PL/pgSQL variable available inside the function body and nowhere else, and OLD
-- is not valid in an INSERT trigger's WHEN at all. A combined trigger carrying
-- the condition below fails at CREATE TRIGGER time.
DROP TRIGGER IF EXISTS trg_camera_status_history ON federated_camera;
DROP TRIGGER IF EXISTS trg_camera_status_history_insert ON federated_camera;
DROP TRIGGER IF EXISTS trg_camera_status_history_update ON federated_camera;

-- Every first observation is a transition: there is no previous status to
-- compare against, so there is no condition to test.
CREATE TRIGGER trg_camera_status_history_insert
    AFTER INSERT ON federated_camera
    FOR EACH ROW
    EXECUTE FUNCTION record_camera_status_change();

-- IS DISTINCT FROM, not <>: is_recording is nullable, and <> against NULL is
-- NULL, so a camera going from "recording" to "unknown" would produce no row.
--
-- The WHEN clause is evaluated before the function body, which is what makes
-- this affordable: the status poll updates every camera every 30 seconds, and
-- all but a handful of those updates change nothing and never enter the
-- function at all.
CREATE TRIGGER trg_camera_status_history_update
    AFTER UPDATE ON federated_camera
    FOR EACH ROW
    WHEN (
        OLD.health       IS DISTINCT FROM NEW.health
        OR OLD.is_enabled   IS DISTINCT FROM NEW.is_enabled
        OR OLD.is_recording IS DISTINCT FROM NEW.is_recording
    )
    EXECUTE FUNCTION record_camera_status_change();

-- Keeps federated_camera.status_changed_at in step with the log above. A BEFORE
-- trigger so the value is written as part of the same row write rather than as
-- a second UPDATE, which would re-fire the AFTER trigger and log a phantom
-- transition.
CREATE OR REPLACE FUNCTION stamp_camera_status_changed_at() RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
BEGIN
    NEW.status_changed_at := NEW.updated_at;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_camera_status_changed_at ON federated_camera;
DROP TRIGGER IF EXISTS trg_camera_status_changed_at_insert ON federated_camera;
DROP TRIGGER IF EXISTS trg_camera_status_changed_at_update ON federated_camera;

CREATE TRIGGER trg_camera_status_changed_at_insert
    BEFORE INSERT ON federated_camera
    FOR EACH ROW
    EXECUTE FUNCTION stamp_camera_status_changed_at();

CREATE TRIGGER trg_camera_status_changed_at_update
    BEFORE UPDATE ON federated_camera
    FOR EACH ROW
    WHEN (
        OLD.health       IS DISTINCT FROM NEW.health
        OR OLD.is_enabled   IS DISTINCT FROM NEW.is_enabled
        OR OLD.is_recording IS DISTINCT FROM NEW.is_recording
    )
    EXECUTE FUNCTION stamp_camera_status_changed_at();

-- Existing rows have a current status but no recorded transition. Stamping them
-- with their last update is the honest reading: it is when we last wrote that
-- status, and leaving NULL would make every pre-upgrade camera look unobserved.
UPDATE federated_camera
   SET status_changed_at = updated_at
 WHERE status_changed_at IS NULL;

-- ---------------------------------------------------------------------------
-- Partition maintenance
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION ensure_camera_status_partitions(
    from_date DATE DEFAULT CURRENT_DATE,
    days_ahead INTEGER DEFAULT 14
) RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    d         DATE;
    part_name TEXT;
    created   INTEGER := 0;
BEGIN
    FOR i IN 0..days_ahead LOOP
        d := from_date + i;
        part_name := format('camera_status_history_%s', to_char(d, 'YYYYMMDD'));

        IF NOT EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = part_name AND n.nspname = 'federation'
        ) THEN
            BEGIN
                EXECUTE format(
                    'CREATE TABLE federation.%I PARTITION OF federation.camera_status_history '
                    'FOR VALUES FROM (%L) TO (%L)', part_name, d, d + 1);
                created := created + 1;
            EXCEPTION WHEN check_violation THEN
                RAISE EXCEPTION
                    'Cannot create partition % : camera_status_history_default holds rows in '
                    'range [%, %). Drain those rows into their correct partition first.',
                    part_name, d, d + 1
                    USING ERRCODE = 'check_violation';
            END;
        END IF;
    END LOOP;

    RETURN created;
END $$;

-- ---------------------------------------------------------------------------
-- Retention
-- ---------------------------------------------------------------------------

-- The cutoff guard matches drop_event_partitions_before: a cutoff at or after
-- today would drop the partition currently being written to, plus every one
-- created ahead of need. Immediate, irreversible, and from a single mistyped
-- setting. The application validates its configuration too; this is the
-- backstop that holds even when the caller is psql.
CREATE OR REPLACE FUNCTION drop_camera_status_partitions_before(cutoff DATE)
RETURNS TABLE (dropped_partition TEXT)
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    part   RECORD;
    bound  DATE;
BEGIN
    IF cutoff IS NULL OR cutoff >= CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing to drop camera status partitions with cutoff % : it is not in the past. '
            'That would drop the partition being written to right now.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    FOR part IN
        SELECT c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'federation'
          AND c.relname ~ '^camera_status_history_[0-9]{8}$'
    LOOP
        bound := to_date(right(part.relname, 8), 'YYYYMMDD');

        IF bound < cutoff THEN
            EXECUTE format('DROP TABLE federation.%I', part.relname);
            dropped_partition := part.relname;
            RETURN NEXT;
        END IF;
    END LOOP;
END $$;

-- ---------------------------------------------------------------------------
-- Partitions for today onward, so the first write after this upgrade lands in a
-- real partition rather than the default.
-- ---------------------------------------------------------------------------

SELECT ensure_camera_status_partitions(CURRENT_DATE, 14);
