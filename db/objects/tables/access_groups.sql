-- Name: access_groups; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.access_groups (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code character varying(50) NOT NULL,
    name character varying(255) NOT NULL,
    description text,
    role_id uuid NOT NULL,
    status character varying(20) DEFAULT 'DRAFT'::character varying NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    updated_by uuid,
    CONSTRAINT access_groups_status_check CHECK (((status)::text = ANY ((ARRAY['DRAFT'::character varying, 'ACTIVE'::character varying, 'INACTIVE'::character varying])::text[])))
);

--
-- Name: COLUMN access_groups.status; Type: COMMENT; Schema: federation; Owner: -
--

COMMENT ON COLUMN federation.access_groups.status IS 'DRAFT -> ACTIVE -> INACTIVE. An INACTIVE group grants nothing (authz functions only consider ACTIVE groups); membership rows survive so re-activation restores the roster. Renamed from DISABLED in v1.12.';

--
-- Name: access_groups access_groups_code_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.access_groups
    ADD CONSTRAINT access_groups_code_key UNIQUE (code);

--
-- Name: access_groups access_groups_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.access_groups
    ADD CONSTRAINT access_groups_pkey PRIMARY KEY (id);

--
-- Name: access_groups access_groups_created_by_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.access_groups
    ADD CONSTRAINT access_groups_created_by_fkey FOREIGN KEY (created_by) REFERENCES federation.platform_users(id);

--
-- Name: access_groups access_groups_role_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.access_groups
    ADD CONSTRAINT access_groups_role_id_fkey FOREIGN KEY (role_id) REFERENCES federation.roles(id);

--
-- Name: access_groups access_groups_updated_by_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.access_groups
    ADD CONSTRAINT access_groups_updated_by_fkey FOREIGN KEY (updated_by) REFERENCES federation.platform_users(id);
