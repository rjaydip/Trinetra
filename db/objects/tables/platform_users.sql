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
    organization_unit_id uuid,
    geographic_area_id uuid,
    designation character varying(150),
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

--
-- Name: platform_users platform_users_organization_unit_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.platform_users
    ADD CONSTRAINT platform_users_organization_unit_id_fkey FOREIGN KEY (organization_unit_id) REFERENCES federation.organization_units(id);

--
-- Name: platform_users platform_users_geographic_area_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.platform_users
    ADD CONSTRAINT platform_users_geographic_area_id_fkey FOREIGN KEY (geographic_area_id) REFERENCES federation.geographic_areas(id);

--
-- Name: COLUMN platform_users.organization_unit_id; Type: COMMENT; Schema: federation; Owner: -
--

COMMENT ON COLUMN federation.platform_users.organization_unit_id IS 'DESCRIPTIVE ONLY. The home organization unit this person belongs to, for display and reporting. Never an authorization input: not read by has_permission, authorized_org_units, authorized_geographic_areas, unscoped_permissions or any scope predicate. Access is granted entirely through access_groups (invariant 12).';

--
-- Name: COLUMN platform_users.geographic_area_id; Type: COMMENT; Schema: federation; Owner: -
--

COMMENT ON COLUMN federation.platform_users.geographic_area_id IS 'DESCRIPTIVE ONLY. The home/coverage geographic area for this person, for display and reporting. Never an authorization input: not read by has_permission, authorized_org_units, authorized_geographic_areas, unscoped_permissions or any scope predicate. Organization and geography are independent scope dimensions (invariant 12).';

--
-- Name: COLUMN platform_users.designation; Type: COMMENT; Schema: federation; Owner: -
--

COMMENT ON COLUMN federation.platform_users.designation IS 'DESCRIPTIVE ONLY. Free-text job title, not a controlled vocabulary and not an authorization input.';
