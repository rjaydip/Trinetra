-- Name: user_groups; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.user_groups (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    group_id uuid NOT NULL,
    assigned_at timestamp with time zone DEFAULT now() NOT NULL,
    assigned_by uuid,
    expires_at timestamp with time zone,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    CONSTRAINT user_groups_status_check CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'REVOKED'::character varying])::text[])))
);

--
-- Name: user_groups user_groups_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.user_groups
    ADD CONSTRAINT user_groups_pkey PRIMARY KEY (id);

--
-- Name: user_groups user_groups_user_id_group_id_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.user_groups
    ADD CONSTRAINT user_groups_user_id_group_id_key UNIQUE (user_id, group_id);

--
-- Name: ix_user_groups_user; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_user_groups_user ON federation.user_groups USING btree (user_id) WHERE ((status)::text = 'ACTIVE'::text);

--
-- Name: user_groups user_groups_assigned_by_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.user_groups
    ADD CONSTRAINT user_groups_assigned_by_fkey FOREIGN KEY (assigned_by) REFERENCES federation.platform_users(id);

--
-- Name: user_groups user_groups_group_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.user_groups
    ADD CONSTRAINT user_groups_group_id_fkey FOREIGN KEY (group_id) REFERENCES federation.access_groups(id) ON DELETE CASCADE;

--
-- Name: user_groups user_groups_user_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.user_groups
    ADD CONSTRAINT user_groups_user_id_fkey FOREIGN KEY (user_id) REFERENCES federation.platform_users(id) ON DELETE CASCADE;
