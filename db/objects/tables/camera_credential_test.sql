--
-- Name: camera_credential_test; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.17 (authenticated credential test for an already-registered camera)
--

CREATE TABLE federation.camera_credential_test (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    camera_id uuid NOT NULL,
    protocol character varying(50) NOT NULL,
    ip_address inet NOT NULL,
    port integer NOT NULL,
    credential_reference text NOT NULL,
    status character varying(20) DEFAULT 'pending'::character varying NOT NULL,
    requested_by text NOT NULL,
    requested_at timestamp with time zone DEFAULT now() NOT NULL,
    executed_by text,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    result jsonb,
    failure_reason text,
    CONSTRAINT camera_credential_test_port_check CHECK (((port >= 1) AND (port <= 65535))),
    CONSTRAINT camera_credential_test_protocol_check CHECK (((protocol)::text = ANY (ARRAY['RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER']::text[]))),
    CONSTRAINT camera_credential_test_status_check CHECK (((status)::text = ANY (ARRAY['pending','running','completed','failed','abandoned']::text[])))
);

--
-- Name: camera_credential_test camera_credential_test_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.camera_credential_test
    ADD CONSTRAINT camera_credential_test_pkey PRIMARY KEY (id);

--
-- Name: camera_credential_test camera_credential_test_camera_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.camera_credential_test
    ADD CONSTRAINT camera_credential_test_camera_id_fkey FOREIGN KEY (camera_id) REFERENCES federation.cameras(id) ON DELETE CASCADE;

--
-- Name: ix_camera_credential_test_camera; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_camera_credential_test_camera ON federation.camera_credential_test USING btree (camera_id, requested_at DESC);

--
-- Name: ix_camera_credential_test_active; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_camera_credential_test_active ON federation.camera_credential_test USING btree (started_at) WHERE ((status)::text = ANY (ARRAY['pending','running']::text[]));

--
-- Name: ix_camera_credential_test_requested; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_camera_credential_test_requested ON federation.camera_credential_test USING btree (requested_at DESC);
