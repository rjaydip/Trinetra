-- Name: secret; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.secret (
    credential_reference text NOT NULL,
    ciphertext bytea NOT NULL,
    nonce bytea NOT NULL,
    tag bytea NOT NULL,
    key_id text NOT NULL,
    description text,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    rotated_at timestamp with time zone,
    CONSTRAINT secret_nonce_check CHECK ((octet_length(nonce) = 12)),
    CONSTRAINT secret_tag_check CHECK ((octet_length(tag) = 16))
);

--
-- Name: secret secret_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.secret
    ADD CONSTRAINT secret_pkey PRIMARY KEY (credential_reference);

--
-- Name: ix_secret_key_id; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_secret_key_id ON federation.secret USING btree (key_id);
