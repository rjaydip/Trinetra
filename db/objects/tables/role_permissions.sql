-- Name: role_permissions; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.role_permissions (
    role_id uuid NOT NULL,
    permission_code character varying(100) NOT NULL
);

--
-- Name: role_permissions role_permissions_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.role_permissions
    ADD CONSTRAINT role_permissions_pkey PRIMARY KEY (role_id, permission_code);

--
-- Name: role_permissions role_permissions_permission_code_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.role_permissions
    ADD CONSTRAINT role_permissions_permission_code_fkey FOREIGN KEY (permission_code) REFERENCES federation.permissions(code);

--
-- Name: role_permissions role_permissions_role_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.role_permissions
    ADD CONSTRAINT role_permissions_role_id_fkey FOREIGN KEY (role_id) REFERENCES federation.roles(id) ON DELETE CASCADE;
