-- Name: permissions; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.permissions (
    code character varying(100) NOT NULL,
    name character varying(255) NOT NULL,
    category character varying(50) NOT NULL,
    description text
);

--
-- Name: permissions permissions_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.permissions
    ADD CONSTRAINT permissions_pkey PRIMARY KEY (code);
