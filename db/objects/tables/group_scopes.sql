-- Name: group_scopes; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.group_scopes (
    group_id uuid NOT NULL,
    scope_id uuid NOT NULL
);

--
-- Name: group_scopes group_scopes_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.group_scopes
    ADD CONSTRAINT group_scopes_pkey PRIMARY KEY (group_id, scope_id);

--
-- Name: group_scopes group_scopes_group_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.group_scopes
    ADD CONSTRAINT group_scopes_group_id_fkey FOREIGN KEY (group_id) REFERENCES federation.access_groups(id) ON DELETE CASCADE;

--
-- Name: group_scopes group_scopes_scope_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.group_scopes
    ADD CONSTRAINT group_scopes_scope_id_fkey FOREIGN KEY (scope_id) REFERENCES federation.scopes(id) ON DELETE CASCADE;
