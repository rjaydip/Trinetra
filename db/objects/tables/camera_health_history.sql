--
-- Name: camera_health_history; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.6. Transitions only — not partitioned, unlike the
-- per-poll camera_status_history.
--

CREATE TABLE federation.camera_health_history (
    id bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
    camera_id uuid NOT NULL,
    operational_status character varying(30) NOT NULL,
    connectivity_status character varying(30) NOT NULL,
    checked_at timestamp with time zone DEFAULT now() NOT NULL,
    latency_ms integer,
    error_code character varying(100),
    failure_reason text,
    source character varying(20) DEFAULT 'MANUAL'::character varying NOT NULL,
    details jsonb DEFAULT '{}'::jsonb NOT NULL,
    recorded_by uuid,
    CONSTRAINT camera_health_history_latency_ms_check CHECK ((latency_ms >= 0)),
    CONSTRAINT camera_health_history_operational_status_check CHECK (((operational_status)::text = ANY (ARRAY['ONLINE','OFFLINE','DEGRADED','UNKNOWN']::text[]))),
    CONSTRAINT camera_health_history_connectivity_status_check CHECK (((connectivity_status)::text = ANY (ARRAY['CONNECTED','DISCONNECTED','UNKNOWN']::text[]))),
    CONSTRAINT camera_health_history_source_check CHECK (((source)::text = ANY (ARRAY['MANUAL','FEDERATION','AI_WORKER','PROBE']::text[])))
);

ALTER TABLE ONLY federation.camera_health_history
    ADD CONSTRAINT camera_health_history_pkey PRIMARY KEY (id);

CREATE INDEX ix_camera_health_history_camera ON federation.camera_health_history USING btree (camera_id, checked_at DESC);

ALTER TABLE ONLY federation.camera_health_history
    ADD CONSTRAINT camera_health_history_camera_id_fkey FOREIGN KEY (camera_id) REFERENCES federation.cameras(id) ON DELETE CASCADE;
