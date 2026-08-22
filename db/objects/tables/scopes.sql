-- Name: scopes; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.scopes (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    scope_type character varying(20) NOT NULL,
    organization_unit_id uuid,
    geographic_area_id uuid,
    resource_type character varying(50),
    resource_id uuid,
    description character varying(255),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_scope_single_dimension CHECK (((((scope_type)::text = 'ORGANIZATION'::text) AND (organization_unit_id IS NOT NULL) AND (geographic_area_id IS NULL) AND (resource_id IS NULL)) OR (((scope_type)::text = 'GEOGRAPHY'::text) AND (geographic_area_id IS NOT NULL) AND (organization_unit_id IS NULL) AND (resource_id IS NULL)) OR (((scope_type)::text = 'RESOURCE'::text) AND (resource_type IS NOT NULL) AND (resource_id IS NOT NULL) AND (organization_unit_id IS NULL) AND (geographic_area_id IS NULL)))),
    CONSTRAINT scopes_scope_type_check CHECK (((scope_type)::text = ANY ((ARRAY['ORGANIZATION'::character varying, 'GEOGRAPHY'::character varying, 'RESOURCE'::character varying])::text[])))
);

--
-- Name: scopes scopes_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.scopes
    ADD CONSTRAINT scopes_pkey PRIMARY KEY (id);

--
-- Name: scopes scopes_geographic_area_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.scopes
    ADD CONSTRAINT scopes_geographic_area_id_fkey FOREIGN KEY (geographic_area_id) REFERENCES federation.geographic_areas(id);

--
-- Name: scopes scopes_organization_unit_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.scopes
    ADD CONSTRAINT scopes_organization_unit_id_fkey FOREIGN KEY (organization_unit_id) REFERENCES federation.organization_units(id);
