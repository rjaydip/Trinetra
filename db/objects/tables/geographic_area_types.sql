-- Name: geographic_area_types; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.geographic_area_types (
    code character varying(50) NOT NULL,
    name character varying(255) NOT NULL,
    level_order integer NOT NULL,
    status character varying(20) DEFAULT 'ACTIVE'::character varying NOT NULL,
    CONSTRAINT geographic_area_types_status_check CHECK (((status)::text = ANY ((ARRAY['ACTIVE'::character varying, 'INACTIVE'::character varying])::text[])))
);

--
-- Name: geographic_area_types geographic_area_types_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.geographic_area_types
    ADD CONSTRAINT geographic_area_types_pkey PRIMARY KEY (code);
