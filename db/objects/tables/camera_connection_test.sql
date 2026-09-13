--
-- Name: camera_connection_test; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.16 (standalone camera reachability probe)
--

CREATE TABLE federation.camera_connection_test (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    ip_address inet NOT NULL,
    port integer NOT NULL,
    protocol character varying(50) NOT NULL,
    status character varying(20) DEFAULT 'pending'::character varying NOT NULL,
    requested_by text NOT NULL,
    requested_at timestamp with time zone DEFAULT now() NOT NULL,
    executed_by text,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    result jsonb,
    failure_reason text,
    CONSTRAINT camera_connection_test_port_check CHECK (((port >= 1) AND (port <= 65535))),
    CONSTRAINT camera_connection_test_protocol_check CHECK (((protocol)::text = ANY (ARRAY['RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER']::text[]))),
    CONSTRAINT camera_connection_test_status_check CHECK (((status)::text = ANY (ARRAY['pending','running','completed','failed','abandoned']::text[])))
);

--
-- Name: camera_connection_test camera_connection_test_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.camera_connection_test
    ADD CONSTRAINT camera_connection_test_pkey PRIMARY KEY (id);

--
-- Name: ix_camera_connection_test_active; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_camera_connection_test_active ON federation.camera_connection_test USING btree (started_at) WHERE ((status)::text = ANY (ARRAY['pending','running']::text[]));

--
-- Name: ix_camera_connection_test_requested; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_camera_connection_test_requested ON federation.camera_connection_test USING btree (requested_at DESC);
