--
-- Name: camera_status_history; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.1. RANGE-partitioned by changed_at; daily partitions
-- (camera_status_history_YYYYMMDD) via federation.ensure_camera_status_partitions. Rows
-- written by the trg_camera_status_history_* triggers on federated_camera. No FK on purpose.
--

CREATE TABLE federation.camera_status_history (
    target_id uuid NOT NULL,
    native_camera_id text NOT NULL,
    changed_at timestamp with time zone NOT NULL,
    organization_unit_id uuid NOT NULL,
    site_id uuid,
    previous_health federation.health_status,
    health federation.health_status NOT NULL,
    previous_enabled boolean,
    is_enabled boolean NOT NULL,
    previous_recording boolean,
    is_recording boolean
)
PARTITION BY RANGE (changed_at);

ALTER TABLE ONLY federation.camera_status_history
    ADD CONSTRAINT camera_status_history_pkey PRIMARY KEY (changed_at, target_id, native_camera_id);

CREATE TABLE federation.camera_status_history_default
    PARTITION OF federation.camera_status_history DEFAULT;

CREATE INDEX ix_camera_status_history_camera ON federation.camera_status_history USING btree (target_id, native_camera_id, changed_at DESC);
CREATE INDEX ix_camera_status_history_org ON federation.camera_status_history USING btree (organization_unit_id, changed_at DESC);
