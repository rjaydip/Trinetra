--
-- Name: watchlist_entry; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.2. Plate-number watchlist, scoped per organization unit. One active entry per
-- (organization_unit_id, plate_number_normalized).
--

CREATE TABLE federation.watchlist_entry (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    organization_unit_id uuid NOT NULL,
    plate_number_normalized text NOT NULL,
    reason text,
    severity text DEFAULT 'Medium'::text NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    created_by uuid,
    CONSTRAINT watchlist_entry_severity_check CHECK ((severity = ANY (ARRAY['Low'::text, 'Medium'::text, 'High'::text, 'Critical'::text])))
);

ALTER TABLE ONLY federation.watchlist_entry
    ADD CONSTRAINT watchlist_entry_pkey PRIMARY KEY (id);

CREATE UNIQUE INDEX ux_watchlist_active ON federation.watchlist_entry USING btree (organization_unit_id, plate_number_normalized) WHERE is_active;

ALTER TABLE ONLY federation.watchlist_entry
    ADD CONSTRAINT watchlist_entry_organization_unit_id_fkey FOREIGN KEY (organization_unit_id) REFERENCES federation.organization_units(id);
ALTER TABLE ONLY federation.watchlist_entry
    ADD CONSTRAINT watchlist_entry_created_by_fkey FOREIGN KEY (created_by) REFERENCES federation.platform_users(id);
