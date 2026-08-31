--
-- Name: maintenance_records; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.6.
--

CREATE TABLE federation.maintenance_records (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    camera_id uuid NOT NULL,
    maintenance_type character varying(40) NOT NULL,
    status character varying(20) DEFAULT 'OPEN'::character varying NOT NULL,
    description text NOT NULL,
    failure_reason text,
    reported_at timestamp with time zone DEFAULT now() NOT NULL,
    started_at timestamp with time zone,
    completed_at timestamp with time zone,
    next_due_at timestamp with time zone,
    performed_by character varying(255),
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid NOT NULL,
    updated_by uuid NOT NULL,
    CONSTRAINT ck_maintenance_records_completed_at CHECK (((status = 'COMPLETED'::text) = (completed_at IS NOT NULL))),
    CONSTRAINT ck_maintenance_records_time_order CHECK (((started_at IS NULL) OR (started_at >= reported_at))),
    CONSTRAINT maintenance_records_maintenance_type_check CHECK (((maintenance_type)::text = ANY (ARRAY['PREVENTIVE','CORRECTIVE','INSPECTION','INSTALLATION','RELOCATION','DECOMMISSION','OTHER']::text[]))),
    CONSTRAINT maintenance_records_status_check CHECK (((status)::text = ANY (ARRAY['OPEN','IN_PROGRESS','COMPLETED','CANCELLED']::text[])))
);

ALTER TABLE ONLY federation.maintenance_records
    ADD CONSTRAINT maintenance_records_pkey PRIMARY KEY (id);

CREATE INDEX ix_maintenance_records_camera ON federation.maintenance_records USING btree (camera_id, reported_at DESC);
CREATE INDEX ix_maintenance_records_open ON federation.maintenance_records USING btree (camera_id) WHERE ((status)::text = ANY (ARRAY['OPEN','IN_PROGRESS']::text[]));
CREATE INDEX ix_maintenance_records_due ON federation.maintenance_records USING btree (next_due_at) WHERE (((status)::text <> 'CANCELLED'::text) AND (next_due_at IS NOT NULL));

ALTER TABLE ONLY federation.maintenance_records
    ADD CONSTRAINT maintenance_records_camera_id_fkey FOREIGN KEY (camera_id) REFERENCES federation.cameras(id) ON DELETE CASCADE;
