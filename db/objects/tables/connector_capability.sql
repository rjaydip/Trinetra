-- Name: connector_capability; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.connector_capability (
    target_id uuid NOT NULL,
    supported integer NOT NULL,
    adapter_version text NOT NULL,
    probed_at timestamp with time zone NOT NULL,
    notes jsonb DEFAULT '{}'::jsonb NOT NULL
);

--
-- Name: connector_capability connector_capability_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_capability
    ADD CONSTRAINT connector_capability_pkey PRIMARY KEY (target_id);

--
-- Name: connector_capability connector_capability_target_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_capability
    ADD CONSTRAINT connector_capability_target_id_fkey FOREIGN KEY (target_id) REFERENCES federation.connector_target(id) ON DELETE CASCADE;
