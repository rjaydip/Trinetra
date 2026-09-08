-- Name: roles; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.roles (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code character varying(50) NOT NULL,
    name character varying(255) NOT NULL,
    description text,
    is_system boolean DEFAULT false NOT NULL,
    status character varying(20) DEFAULT 'DRAFT'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    customized_at timestamp with time zone,
    CONSTRAINT roles_status_check CHECK (((status)::text = ANY ((ARRAY['DRAFT'::character varying, 'ACTIVE'::character varying, 'INACTIVE'::character varying])::text[])))
);

--
-- Name: COLUMN roles.status; Type: COMMENT; Schema: federation; Owner: -
--

COMMENT ON COLUMN federation.roles.status IS 'DRAFT (composed, not yet granting) -> ACTIVE -> INACTIVE. A custom role is created DRAFT by the API; presets are seeded ACTIVE. DELETE soft-deletes to INACTIVE. Every authz function requires status = ''ACTIVE'', so DRAFT and INACTIVE grant nothing.';

--
-- Name: COLUMN roles.customized_at; Type: COMMENT; Schema: federation; Owner: -
--

COMMENT ON COLUMN federation.roles.customized_at IS 'Set by the API on the first edit of a preset (is_system) role. Migrations that re-seed preset roles must skip rows where this is non-NULL.';

--
-- Name: roles roles_code_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.roles
    ADD CONSTRAINT roles_code_key UNIQUE (code);

--
-- Name: roles roles_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.roles
    ADD CONSTRAINT roles_pkey PRIMARY KEY (id);
