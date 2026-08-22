-- Name: sites; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.sites (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code character varying(100) NOT NULL,
    name character varying(255) NOT NULL,
    geographic_area_id uuid NOT NULL,
    site_type character varying(50),
    address text,
    latitude numeric(10,7),
    longitude numeric(10,7),
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    metadata jsonb DEFAULT '{}'::jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT sites_latitude_check CHECK (((latitude >= ('-90'::integer)::numeric) AND (latitude <= (90)::numeric))),
    CONSTRAINT sites_longitude_check CHECK (((longitude >= ('-180'::integer)::numeric) AND (longitude <= (180)::numeric))),
    CONSTRAINT sites_status_check CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'INACTIVE'::character varying])::text[])))
);

--
-- Name: sites sites_code_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.sites
    ADD CONSTRAINT sites_code_key UNIQUE (code);

--
-- Name: sites sites_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.sites
    ADD CONSTRAINT sites_pkey PRIMARY KEY (id);

--
-- Name: ix_site_area; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_site_area ON federation.sites USING btree (geographic_area_id);

--
-- Name: sites sites_geographic_area_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.sites
    ADD CONSTRAINT sites_geographic_area_id_fkey FOREIGN KEY (geographic_area_id) REFERENCES federation.geographic_areas(id);
