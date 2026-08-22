-- Name: federated_camera; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.federated_camera (
    target_id uuid NOT NULL,
    native_camera_id text NOT NULL,
    camera_id uuid,
    organization_unit_id uuid NOT NULL,
    site_id uuid,
    name text,
    vendor_model text,
    firmware text,
    latitude numeric(10,7),
    longitude numeric(10,7),
    is_enabled boolean DEFAULT true NOT NULL,
    is_recording boolean,
    health federation.health_status DEFAULT 'Unknown'::federation.health_status NOT NULL,
    last_seen timestamp with time zone,
    stream_references text[] DEFAULT '{}'::text[] NOT NULL,
    raw_reference text,
    first_seen_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT federated_camera_latitude_check CHECK (((latitude >= ('-90'::integer)::numeric) AND (latitude <= (90)::numeric))),
    CONSTRAINT federated_camera_longitude_check CHECK (((longitude >= ('-180'::integer)::numeric) AND (longitude <= (180)::numeric)))
);

--
-- Name: federated_camera federated_camera_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.federated_camera
    ADD CONSTRAINT federated_camera_pkey PRIMARY KEY (target_id, native_camera_id);

--
-- Name: ix_camera_org; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_camera_org ON federation.federated_camera USING btree (organization_unit_id);

--
-- Name: ix_camera_registry_id; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_camera_registry_id ON federation.federated_camera USING btree (camera_id) WHERE (camera_id IS NOT NULL);

--
-- Name: ix_camera_unreconciled; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_camera_unreconciled ON federation.federated_camera USING btree (target_id) WHERE (camera_id IS NULL);

--
-- Name: federated_camera federated_camera_organization_unit_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.federated_camera
    ADD CONSTRAINT federated_camera_organization_unit_id_fkey FOREIGN KEY (organization_unit_id) REFERENCES federation.organization_units(id);

--
-- Name: federated_camera federated_camera_site_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.federated_camera
    ADD CONSTRAINT federated_camera_site_id_fkey FOREIGN KEY (site_id) REFERENCES federation.sites(id);

--
-- Name: federated_camera federated_camera_target_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.federated_camera
    ADD CONSTRAINT federated_camera_target_id_fkey FOREIGN KEY (target_id) REFERENCES federation.connector_target(id) ON DELETE CASCADE;
