--
-- Name: refresh_token; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.refresh_token (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    token_hash text NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    used_at timestamp with time zone,
    replaced_by uuid,
    revoked_at timestamp with time zone
);

--
-- Name: refresh_token refresh_token_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.refresh_token
    ADD CONSTRAINT refresh_token_pkey PRIMARY KEY (id);

--
-- Name: refresh_token refresh_token_token_hash_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.refresh_token
    ADD CONSTRAINT refresh_token_token_hash_key UNIQUE (token_hash);

--
-- Name: ix_refresh_token_user; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_refresh_token_user ON federation.refresh_token USING btree (user_id);

--
-- Name: ix_refresh_token_expires; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_refresh_token_expires ON federation.refresh_token USING btree (expires_at);

--
-- Name: refresh_token refresh_token_user_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.refresh_token
    ADD CONSTRAINT refresh_token_user_id_fkey FOREIGN KEY (user_id) REFERENCES federation.platform_users(id) ON DELETE CASCADE;
