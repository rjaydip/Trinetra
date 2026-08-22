-- Name: config_audit; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.config_audit (
    id bigint NOT NULL,
    changed_at timestamp with time zone DEFAULT now() NOT NULL,
    actor text NOT NULL,
    actor_user_id uuid,
    actor_api_key_id uuid,
    action text NOT NULL,
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    organization_unit_id uuid,
    before_state jsonb,
    after_state jsonb,
    source_address text
)
PARTITION BY RANGE (changed_at);

--
-- Name: config_audit_id_seq; Type: SEQUENCE; Schema: federation; Owner: -
--

ALTER TABLE federation.config_audit ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME federation.config_audit_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);

--
-- Name: config_audit config_audit_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.config_audit
    ADD CONSTRAINT config_audit_pkey PRIMARY KEY (changed_at, id);

--
-- Name: ix_config_audit_actor; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_config_audit_actor ON ONLY federation.config_audit USING btree (actor, changed_at DESC);

--
-- Name: ix_config_audit_entity; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_config_audit_entity ON ONLY federation.config_audit USING btree (entity_type, entity_id, changed_at DESC);
