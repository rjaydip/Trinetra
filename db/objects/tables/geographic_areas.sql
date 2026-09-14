-- Name: geographic_areas; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.geographic_areas (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    parent_area_id uuid,
    code character varying(50) NOT NULL,
    name character varying(255) NOT NULL,
    area_type character varying(50) NOT NULL,
    description text,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    metadata jsonb DEFAULT '{}'::jsonb NOT NULL,
    -- v1.19: surveyed boundary polygon (PostGIS). Nullable — most areas have no surveyed
    -- boundary on day one; only GET /gis/gaps requires it, and reports "no boundary set" rather
    -- than guessing when it is absent. Written only through
    -- POST /api/v1/geographic-areas/bulk-import-boundaries, never a per-area form field.
    boundary geometry(Polygon, 4326),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT geographic_areas_status_check CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'INACTIVE'::character varying])::text[])))
);

--
-- Name: geographic_areas geographic_areas_parent_code_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.geographic_areas
    ADD CONSTRAINT geographic_areas_parent_code_key UNIQUE NULLS NOT DISTINCT (parent_area_id, code);

--
-- Name: geographic_areas geographic_areas_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.geographic_areas
    ADD CONSTRAINT geographic_areas_pkey PRIMARY KEY (id);

--
-- Name: ix_geo_area_parent; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_geo_area_parent ON federation.geographic_areas USING btree (parent_area_id);

--
-- Name: ix_geo_area_type; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_geo_area_type ON federation.geographic_areas USING btree (area_type);

--
-- Name: ix_geographic_areas_boundary; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_geographic_areas_boundary ON federation.geographic_areas USING gist (boundary);

--
-- Name: geographic_areas trg_geo_area_acyclic; Type: TRIGGER; Schema: federation; Owner: -
--

CREATE TRIGGER trg_geo_area_acyclic BEFORE INSERT OR UPDATE OF parent_area_id, area_type ON federation.geographic_areas FOR EACH ROW EXECUTE FUNCTION federation.assert_geographic_area_acyclic();

--
-- Name: geographic_areas geographic_areas_parent_area_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.geographic_areas
    ADD CONSTRAINT geographic_areas_parent_area_id_fkey FOREIGN KEY (parent_area_id) REFERENCES federation.geographic_areas(id);

--
-- Name: geographic_areas geographic_areas_area_type_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.geographic_areas
    ADD CONSTRAINT geographic_areas_area_type_fkey FOREIGN KEY (area_type) REFERENCES federation.geographic_area_types(code);
