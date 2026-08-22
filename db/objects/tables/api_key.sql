-- Name: api_key; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.api_key (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    key_id character varying(100) NOT NULL,
    key_hash text NOT NULL,
    display_name character varying(255) NOT NULL,
    group_id uuid NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    expires_at timestamp with time zone,
    revoked_at timestamp with time zone,
    last_used_at timestamp with time zone
);

--
-- Name: api_key api_key_key_id_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.api_key
    ADD CONSTRAINT api_key_key_id_key UNIQUE (key_id);

--
-- Name: api_key api_key_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.api_key
    ADD CONSTRAINT api_key_pkey PRIMARY KEY (id);

--
-- Name: ix_api_key_hash; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_api_key_hash ON federation.api_key USING btree (key_hash) WHERE (revoked_at IS NULL);

--
-- Name: api_key api_key_created_by_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.api_key
    ADD CONSTRAINT api_key_created_by_fkey FOREIGN KEY (created_by) REFERENCES federation.platform_users(id);

--
-- Name: api_key api_key_group_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.api_key
    ADD CONSTRAINT api_key_group_id_fkey FOREIGN KEY (group_id) REFERENCES federation.access_groups(id);
