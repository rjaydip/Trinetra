-- Name: organizations; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.organizations (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code character varying(50) NOT NULL,
    name character varying(255) NOT NULL,
    organization_type character varying(50) NOT NULL,
    description text,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT organizations_status_check CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'INACTIVE'::character varying])::text[])))
);

--
-- Name: organizations organizations_code_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.organizations
    ADD CONSTRAINT organizations_code_key UNIQUE (code);

--
-- Name: organizations organizations_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.organizations
    ADD CONSTRAINT organizations_pkey PRIMARY KEY (id);
