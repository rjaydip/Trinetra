--
-- Name: cameras; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.6 (Centralised CCTV Registry & GIS Mapping)
--

CREATE TABLE federation.cameras (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    camera_code character varying(100) NOT NULL,
    name character varying(255) NOT NULL,
    organization_unit_id uuid NOT NULL,
    site_id uuid NOT NULL,
    manufacturer character varying(255),
    model character varying(255),
    camera_type character varying(50) NOT NULL,
    serial_number character varying(255),
    latitude numeric(10,7) NOT NULL,
    longitude numeric(10,7) NOT NULL,
    altitude numeric(10,3),
    mounting_height numeric(10,3),
    azimuth numeric(7,3),
    tilt numeric(7,3),
    horizontal_fov numeric(7,3),
    vertical_fov numeric(7,3),
    effective_range numeric(10,3),
    ip_address inet,
    port integer,
    protocol character varying(50),
    vms_id uuid,
    stream_reference character varying(512),
    credential_reference character varying(255),
    installation_date date,
    operational_status character varying(30) DEFAULT 'UNKNOWN'::character varying NOT NULL,
    connectivity_status character varying(30) DEFAULT 'UNKNOWN'::character varying NOT NULL,
    maintenance_status character varying(30) DEFAULT 'NORMAL'::character varying NOT NULL,
    last_seen_at timestamp with time zone,
    last_health_check_at timestamp with time zone,
    deleted_at timestamp with time zone,
    deleted_by uuid,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid NOT NULL,
    updated_by uuid NOT NULL,
    CONSTRAINT ck_cameras_retire_consistent CHECK (((maintenance_status = 'RETIRED'::text) = (deleted_at IS NOT NULL))),
    CONSTRAINT cameras_latitude_check CHECK (((latitude >= ('-90'::integer)::numeric) AND (latitude <= (90)::numeric))),
    CONSTRAINT cameras_longitude_check CHECK (((longitude >= ('-180'::integer)::numeric) AND (longitude <= (180)::numeric))),
    CONSTRAINT cameras_altitude_check CHECK (((altitude >= ('-500'::integer)::numeric) AND (altitude <= (9000)::numeric))),
    CONSTRAINT cameras_mounting_height_check CHECK (((mounting_height >= (0)::numeric) AND (mounting_height <= (200)::numeric))),
    CONSTRAINT cameras_azimuth_check CHECK (((azimuth >= (0)::numeric) AND (azimuth < (360)::numeric))),
    CONSTRAINT cameras_tilt_check CHECK (((tilt >= ('-90'::integer)::numeric) AND (tilt <= (90)::numeric))),
    CONSTRAINT cameras_horizontal_fov_check CHECK (((horizontal_fov > (0)::numeric) AND (horizontal_fov <= (360)::numeric))),
    CONSTRAINT cameras_vertical_fov_check CHECK (((vertical_fov > (0)::numeric) AND (vertical_fov <= (180)::numeric))),
    CONSTRAINT cameras_effective_range_check CHECK (((effective_range > (0)::numeric) AND (effective_range <= (5000)::numeric))),
    CONSTRAINT cameras_port_check CHECK (((port >= 1) AND (port <= 65535))),
    CONSTRAINT cameras_camera_type_check CHECK (((camera_type)::text = ANY (ARRAY['FIXED','PTZ','DOME','BULLET','ANPR','THERMAL','MULTISENSOR','OTHER']::text[]))),
    CONSTRAINT cameras_protocol_check CHECK (((protocol IS NULL) OR ((protocol)::text = ANY (ARRAY['RTSP','RTSPS','ONVIF','HTTP','HTTPS','RTMP','SRT','OTHER']::text[])))),
    CONSTRAINT cameras_operational_status_check CHECK (((operational_status)::text = ANY (ARRAY['ONLINE','OFFLINE','DEGRADED','UNKNOWN']::text[]))),
    CONSTRAINT cameras_connectivity_status_check CHECK (((connectivity_status)::text = ANY (ARRAY['CONNECTED','DISCONNECTED','UNKNOWN']::text[]))),
    CONSTRAINT cameras_maintenance_status_check CHECK (((maintenance_status)::text = ANY (ARRAY['NORMAL','REQUIRED','UNDER_MAINTENANCE','RETIRED']::text[])))
);

ALTER TABLE ONLY federation.cameras
    ADD CONSTRAINT cameras_pkey PRIMARY KEY (id);

--
-- camera_code is unique among live (non-retired) rows only.
--
CREATE UNIQUE INDEX ux_cameras_code_live ON federation.cameras USING btree (camera_code) WHERE (deleted_at IS NULL);

CREATE INDEX ix_cameras_organization_unit ON federation.cameras USING btree (organization_unit_id);
CREATE INDEX ix_cameras_site ON federation.cameras USING btree (site_id);
CREATE INDEX ix_cameras_camera_type ON federation.cameras USING btree (camera_type);
CREATE INDEX ix_cameras_operational_status ON federation.cameras USING btree (operational_status);
CREATE INDEX ix_cameras_maintenance_status ON federation.cameras USING btree (maintenance_status);
CREATE INDEX ix_cameras_vms ON federation.cameras USING btree (vms_id) WHERE (vms_id IS NOT NULL);
CREATE INDEX ix_cameras_bbox_live ON federation.cameras USING btree (latitude, longitude) WHERE (deleted_at IS NULL);

ALTER TABLE ONLY federation.cameras
    ADD CONSTRAINT cameras_organization_unit_id_fkey FOREIGN KEY (organization_unit_id) REFERENCES federation.organization_units(id);

ALTER TABLE ONLY federation.cameras
    ADD CONSTRAINT cameras_site_id_fkey FOREIGN KEY (site_id) REFERENCES federation.sites(id);
