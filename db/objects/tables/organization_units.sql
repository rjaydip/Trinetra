-- Name: organization_units; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.organization_units (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    organization_id uuid NOT NULL,
    parent_unit_id uuid,
    code character varying(50) NOT NULL,
    name character varying(255) NOT NULL,
    unit_type character varying(50) NOT NULL,
    description text,
    geographic_area_id uuid,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    metadata jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT organization_units_status_check CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'INACTIVE'::character varying])::text[])))
);

--
-- Name: organization_units organization_units_code_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.organization_units
    ADD CONSTRAINT organization_units_code_key UNIQUE (code);

--
-- Name: organization_units organization_units_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.organization_units
    ADD CONSTRAINT organization_units_pkey PRIMARY KEY (id);

--
-- Name: ix_org_unit_org; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_org_unit_org ON federation.organization_units USING btree (organization_id);

--
-- Name: ix_org_unit_parent; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_org_unit_parent ON federation.organization_units USING btree (parent_unit_id);

--
-- Name: organization_units trg_org_unit_acyclic; Type: TRIGGER; Schema: federation; Owner: -
--

CREATE TRIGGER trg_org_unit_acyclic BEFORE INSERT OR UPDATE OF parent_unit_id, organization_id ON federation.organization_units FOR EACH ROW EXECUTE FUNCTION federation.assert_org_unit_acyclic();

--
-- Name: organization_units organization_units_organization_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.organization_units
    ADD CONSTRAINT organization_units_organization_id_fkey FOREIGN KEY (organization_id) REFERENCES federation.organizations(id);

--
-- Name: organization_units organization_units_parent_unit_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.organization_units
    ADD CONSTRAINT organization_units_parent_unit_id_fkey FOREIGN KEY (parent_unit_id) REFERENCES federation.organization_units(id);

--
-- Name: ix_org_unit_geo_area; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_org_unit_geo_area ON federation.organization_units USING btree (geographic_area_id) WHERE (geographic_area_id IS NOT NULL);

--
-- Name: organization_units organization_units_geographic_area_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.organization_units
    ADD CONSTRAINT organization_units_geographic_area_id_fkey FOREIGN KEY (geographic_area_id) REFERENCES federation.geographic_areas(id);

--
-- Name: COLUMN organization_units.geographic_area_id; Type: COMMENT; Schema: federation; Owner: -
--

COMMENT ON COLUMN federation.organization_units.geographic_area_id IS 'DESCRIPTIVE ONLY. A single "home area" label for humans and the UI. Never an authorization input: not read by has_permission, authorized_org_units, authorized_geographic_areas, has_unscoped_geography or any scope predicate. Organization and geography are independent scope dimensions (invariant 12).';
