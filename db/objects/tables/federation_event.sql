-- Name: federation_event; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.federation_event (
    event_id text NOT NULL,
    source_vms_id uuid NOT NULL,
    source_event_id text,
    camera_id text NOT NULL,
    organization_unit_id uuid NOT NULL,
    geographic_area_id uuid,
    event_type text NOT NULL,
    vendor_event_type text,
    occurred_at timestamp with time zone NOT NULL,
    severity text DEFAULT 'Info'::text NOT NULL,
    object_reference text,
    confidence double precision,
    latitude numeric(10,7),
    longitude numeric(10,7),
    raw_reference text,
    delivery_mode text DEFAULT 'Live'::text NOT NULL,
    trace_id text,
    received_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT federation_event_confidence_check CHECK (((confidence IS NULL) OR ((confidence >= (0)::double precision) AND (confidence <= (1)::double precision)))),
    CONSTRAINT federation_event_latitude_check CHECK (((latitude >= ('-90'::integer)::numeric) AND (latitude <= (90)::numeric))),
    CONSTRAINT federation_event_longitude_check CHECK (((longitude >= ('-180'::integer)::numeric) AND (longitude <= (180)::numeric)))
)
PARTITION BY RANGE (occurred_at);

--
-- Name: federation_event federation_event_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.federation_event
    ADD CONSTRAINT federation_event_pkey PRIMARY KEY (occurred_at, event_id);

--
-- Name: ix_event_camera_time; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_event_camera_time ON ONLY federation.federation_event USING btree (camera_id, occurred_at DESC);

--
-- Name: ix_event_object_time; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_event_object_time ON ONLY federation.federation_event USING btree (object_reference, occurred_at DESC) WHERE (object_reference IS NOT NULL);

--
-- Name: ix_event_org_time; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_event_org_time ON ONLY federation.federation_event USING btree (organization_unit_id, occurred_at DESC);

--
-- Name: ix_event_type_time; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_event_type_time ON ONLY federation.federation_event USING btree (event_type, occurred_at DESC);

--
-- Name: ux_event_dedup; Type: INDEX; Schema: federation; Owner: -
--

CREATE UNIQUE INDEX ux_event_dedup ON ONLY federation.federation_event USING btree (source_vms_id, source_event_id, occurred_at);
