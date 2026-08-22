-- Name: connection_test; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.connection_test (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    target_id uuid NOT NULL,
    status character varying(20) DEFAULT 'pending'::character varying NOT NULL,
    requested_by text NOT NULL,
    requested_at timestamp with time zone DEFAULT now() NOT NULL,
    executed_by text,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    result jsonb,
    failure_reason text,
    CONSTRAINT connection_test_status_check CHECK (((status)::text = ANY ((ARRAY['pending'::character varying, 'running'::character varying, 'completed'::character varying, 'failed'::character varying, 'abandoned'::character varying])::text[])))
);

--
-- Name: connection_test connection_test_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connection_test
    ADD CONSTRAINT connection_test_pkey PRIMARY KEY (id);

--
-- Name: ix_connection_test_active; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_connection_test_active ON federation.connection_test USING btree (started_at) WHERE ((status)::text = ANY ((ARRAY['pending'::character varying, 'running'::character varying])::text[]));

--
-- Name: ix_connection_test_target; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_connection_test_target ON federation.connection_test USING btree (target_id, requested_at DESC);

--
-- Name: connection_test connection_test_target_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connection_test
    ADD CONSTRAINT connection_test_target_id_fkey FOREIGN KEY (target_id) REFERENCES federation.connector_target(id) ON DELETE CASCADE;
