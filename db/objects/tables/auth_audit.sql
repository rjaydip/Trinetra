-- Name: auth_audit; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.auth_audit (
    id bigint NOT NULL,
    occurred_at timestamp with time zone DEFAULT now() NOT NULL,
    event_type text NOT NULL,
    outcome text NOT NULL,
    user_id uuid,
    api_key_id uuid,
    presented_username text,
    source_address text,
    user_agent text,
    jti text,
    detail jsonb,
    CONSTRAINT auth_audit_outcome_check CHECK ((outcome = ANY (ARRAY['success'::text, 'failure'::text, 'lockout'::text, 'revoked'::text]))),
    CONSTRAINT auth_audit_presented_username_check CHECK (((presented_username IS NULL) OR (char_length(presented_username) <= 256))),
    CONSTRAINT auth_audit_username_only_when_anon CHECK (((presented_username IS NULL) OR (user_id IS NULL)))
)
PARTITION BY RANGE (occurred_at);

--
-- Name: auth_audit_id_seq; Type: SEQUENCE; Schema: federation; Owner: -
--

ALTER TABLE federation.auth_audit ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME federation.auth_audit_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);

--
-- Name: auth_audit auth_audit_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.auth_audit
    ADD CONSTRAINT auth_audit_pkey PRIMARY KEY (occurred_at, id);

--
-- Name: ix_auth_audit_failures; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_auth_audit_failures ON ONLY federation.auth_audit USING btree (occurred_at DESC) WHERE (outcome <> 'success'::text);

--
-- Name: ix_auth_audit_user; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_auth_audit_user ON ONLY federation.auth_audit USING btree (user_id, occurred_at DESC);

--
-- Name: auth_audit auth_audit_api_key_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE federation.auth_audit
    ADD CONSTRAINT auth_audit_api_key_id_fkey FOREIGN KEY (api_key_id) REFERENCES federation.api_key(id) ON DELETE SET NULL;

--
-- Name: auth_audit auth_audit_user_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE federation.auth_audit
    ADD CONSTRAINT auth_audit_user_id_fkey FOREIGN KEY (user_id) REFERENCES federation.platform_users(id) ON DELETE SET NULL;
