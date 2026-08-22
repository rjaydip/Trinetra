-- Name: connector_target; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.connector_target (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code character varying(100) NOT NULL,
    organization_unit_id uuid NOT NULL,
    site_id uuid,
    display_name character varying(255) NOT NULL,
    vendor federation.vendor_kind NOT NULL,
    runtime_class federation.runtime_class DEFAULT 'Managed'::federation.runtime_class NOT NULL,
    endpoint text NOT NULL,
    credential_reference text NOT NULL,
    verify_tls boolean DEFAULT true NOT NULL,
    state federation.target_state DEFAULT 'Active'::federation.target_state NOT NULL,
    rate_limit_per_second double precision DEFAULT 5.0 NOT NULL,
    rate_limit_burst integer DEFAULT 10 NOT NULL,
    inventory_poll_seconds integer DEFAULT 300 NOT NULL,
    status_poll_seconds integer DEFAULT 30 NOT NULL,
    event_poll_seconds integer DEFAULT 10 NOT NULL,
    max_concurrent_requests integer DEFAULT 4 NOT NULL,
    expected_camera_count integer,
    leased_by text,
    lease_expires_at timestamp with time zone,
    last_claimed_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_by uuid,
    CONSTRAINT connector_target_event_poll_seconds_check CHECK ((event_poll_seconds >= 1)),
    CONSTRAINT connector_target_inventory_poll_seconds_check CHECK ((inventory_poll_seconds >= 30)),
    CONSTRAINT connector_target_max_concurrent_requests_check CHECK ((max_concurrent_requests > 0)),
    CONSTRAINT connector_target_rate_limit_burst_check CHECK ((rate_limit_burst >= 1)),
    CONSTRAINT connector_target_rate_limit_per_second_check CHECK ((rate_limit_per_second > (0)::double precision)),
    CONSTRAINT connector_target_status_poll_seconds_check CHECK ((status_poll_seconds >= 5))
);

--
-- Name: connector_target connector_target_code_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_target
    ADD CONSTRAINT connector_target_code_key UNIQUE (code);

--
-- Name: connector_target connector_target_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_target
    ADD CONSTRAINT connector_target_pkey PRIMARY KEY (id);

--
-- Name: ix_target_claimable; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_target_claimable ON federation.connector_target USING btree (lease_expires_at NULLS FIRST, last_claimed_at NULLS FIRST) WHERE (state = 'Active'::federation.target_state);

--
-- Name: ix_target_leased_by; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_target_leased_by ON federation.connector_target USING btree (leased_by) WHERE (leased_by IS NOT NULL);

--
-- Name: ix_target_org; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_target_org ON federation.connector_target USING btree (organization_unit_id);

--
-- Name: ix_target_site; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_target_site ON federation.connector_target USING btree (site_id);

--
-- Name: connector_target connector_target_created_by_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_target
    ADD CONSTRAINT connector_target_created_by_fkey FOREIGN KEY (created_by) REFERENCES federation.platform_users(id);

--
-- Name: connector_target connector_target_organization_unit_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_target
    ADD CONSTRAINT connector_target_organization_unit_id_fkey FOREIGN KEY (organization_unit_id) REFERENCES federation.organization_units(id);

--
-- Name: connector_target connector_target_site_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_target
    ADD CONSTRAINT connector_target_site_id_fkey FOREIGN KEY (site_id) REFERENCES federation.sites(id);

--
-- Name: connector_target connector_target_updated_by_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_target
    ADD CONSTRAINT connector_target_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES federation.platform_users(id);
