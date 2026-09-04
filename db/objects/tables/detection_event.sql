--
-- Name: detection_event; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.2. Model 2 detection ingest (POST /api/v1/detections). RANGE-partitioned by
-- occurred_at; daily partitions via federation.ensure_event_partitions. Idempotent on
-- (event_id, occurred_at).
--

CREATE TABLE federation.detection_event (
    event_id text NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    target_id uuid NOT NULL,
    native_camera_id text NOT NULL,
    camera_id uuid,
    organization_unit_id uuid NOT NULL,
    site_id uuid,
    event_type text NOT NULL,
    confidence double precision,
    vehicle_type text,
    plate_number_raw text,
    plate_number_normalized text,
    snapshot_reference text,
    received_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT detection_event_confidence_check CHECK (((confidence IS NULL) OR ((confidence >= (0)::double precision) AND (confidence <= (1)::double precision))))
)
PARTITION BY RANGE (occurred_at);

ALTER TABLE ONLY federation.detection_event
    ADD CONSTRAINT detection_event_pkey PRIMARY KEY (occurred_at, event_id);

CREATE TABLE federation.detection_event_default PARTITION OF federation.detection_event DEFAULT;

CREATE UNIQUE INDEX ux_detection_event_dedup ON federation.detection_event USING btree (event_id, occurred_at);
CREATE INDEX ix_detection_camera_time ON federation.detection_event USING btree (target_id, native_camera_id, occurred_at DESC);
CREATE INDEX ix_detection_org_time ON federation.detection_event USING btree (organization_unit_id, occurred_at DESC);
CREATE INDEX ix_detection_plate ON federation.detection_event USING btree (plate_number_normalized, occurred_at DESC) WHERE (plate_number_normalized IS NOT NULL);

ALTER TABLE ONLY federation.detection_event
    ADD CONSTRAINT detection_event_target_id_fkey FOREIGN KEY (target_id) REFERENCES federation.connector_target(id) ON DELETE CASCADE;
ALTER TABLE ONLY federation.detection_event
    ADD CONSTRAINT detection_event_organization_unit_id_fkey FOREIGN KEY (organization_unit_id) REFERENCES federation.organization_units(id);
ALTER TABLE ONLY federation.detection_event
    ADD CONSTRAINT detection_event_site_id_fkey FOREIGN KEY (site_id) REFERENCES federation.sites(id);
