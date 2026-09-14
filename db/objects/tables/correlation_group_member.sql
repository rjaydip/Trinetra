-- Name: correlation_group_member; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.correlation_group_member (
    group_id uuid NOT NULL,
    federation_event_id text NOT NULL,
    event_occurred_at timestamp with time zone NOT NULL,
    camera_id text NOT NULL,
    source_vms_id uuid NOT NULL,
    organization_unit_id uuid NOT NULL,
    geographic_area_id uuid
);

COMMENT ON TABLE federation.correlation_group_member IS 'Join table: the federation_event rows that make up one correlation_group. organization_unit_id / geographic_area_id are denormalised from federation_event so a scoped read never joins back to the partitioned event table.';

--
-- Name: correlation_group_member correlation_group_member_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.correlation_group_member
    ADD CONSTRAINT correlation_group_member_pkey PRIMARY KEY (group_id, federation_event_id);

--
-- Name: correlation_group_member correlation_group_member_group_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.correlation_group_member
    ADD CONSTRAINT correlation_group_member_group_id_fkey FOREIGN KEY (group_id) REFERENCES federation.correlation_group(id) ON DELETE CASCADE;

--
-- Name: ix_correlation_member_org; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_correlation_member_org ON federation.correlation_group_member USING btree (group_id, organization_unit_id);

--
-- Name: ix_correlation_member_geo; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_correlation_member_geo ON federation.correlation_group_member USING btree (group_id, geographic_area_id) WHERE (geographic_area_id IS NOT NULL);
