--
-- Name: password_history; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.password_history (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    user_id uuid NOT NULL,
    password_hash bytea NOT NULL,
    password_salt bytea NOT NULL,
    password_iterations integer NOT NULL,
    password_algorithm text NOT NULL,
    set_at timestamp with time zone DEFAULT now() NOT NULL
);

--
-- Name: password_history password_history_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.password_history
    ADD CONSTRAINT password_history_pkey PRIMARY KEY (id);

--
-- Name: ix_password_history_user; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_password_history_user ON federation.password_history USING btree (user_id, set_at DESC);

--
-- Name: password_history password_history_user_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.password_history
    ADD CONSTRAINT password_history_user_id_fkey FOREIGN KEY (user_id) REFERENCES federation.platform_users(id) ON DELETE CASCADE;
