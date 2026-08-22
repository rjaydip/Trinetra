-- Name: credential_access_log; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.credential_access_log (
    id bigint NOT NULL,
    accessed_at timestamp with time zone DEFAULT now() NOT NULL,
    credential_reference text NOT NULL,
    accessed_by text NOT NULL,
    target_id uuid,
    succeeded boolean NOT NULL,
    failure_reason text
)
PARTITION BY RANGE (accessed_at);

--
-- Name: credential_access_log_id_seq; Type: SEQUENCE; Schema: federation; Owner: -
--

ALTER TABLE federation.credential_access_log ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME federation.credential_access_log_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);

--
-- Name: credential_access_log credential_access_log_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.credential_access_log
    ADD CONSTRAINT credential_access_log_pkey PRIMARY KEY (accessed_at, id);

--
-- Name: ix_credential_access_failures; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_credential_access_failures ON ONLY federation.credential_access_log USING btree (accessed_at DESC) WHERE (NOT succeeded);

--
-- Name: ix_credential_access_ref; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_credential_access_ref ON ONLY federation.credential_access_log USING btree (credential_reference, accessed_at DESC);
