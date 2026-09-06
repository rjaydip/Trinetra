-- Name: platform_users; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.platform_users (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    username character varying(100) NOT NULL,
    display_name character varying(255) NOT NULL,
    email character varying(255),
    password_hash bytea NOT NULL,
    password_salt bytea NOT NULL,
    password_iterations integer NOT NULL,
    password_algorithm character varying(50) DEFAULT 'PBKDF2-HMAC-SHA256'::character varying NOT NULL,
    must_change_password boolean DEFAULT false NOT NULL,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    failed_login_count integer DEFAULT 0 NOT NULL,
    locked_until timestamp with time zone,
    last_login_at timestamp with time zone,
    is_system boolean DEFAULT false NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    token_version integer DEFAULT 0 NOT NULL,
    CONSTRAINT platform_users_status_check CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'INACTIVE'::character varying, 'LOCKED'::character varying])::text[])))
);

--
-- Name: platform_users platform_users_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.platform_users
    ADD CONSTRAINT platform_users_pkey PRIMARY KEY (id);

--
-- Name: platform_users platform_users_username_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.platform_users
    ADD CONSTRAINT platform_users_username_key UNIQUE (username);
