-- Name: connector_cursor; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.connector_cursor (
    target_id uuid NOT NULL,
    cursor_timestamp timestamp with time zone NOT NULL,
    cursor_event_id text,
    max_lookback_hours integer DEFAULT 6 NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT connector_cursor_max_lookback_hours_check CHECK ((max_lookback_hours > 0))
);

--
-- Name: connector_cursor connector_cursor_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_cursor
    ADD CONSTRAINT connector_cursor_pkey PRIMARY KEY (target_id);

--
-- Name: connector_cursor connector_cursor_target_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_cursor
    ADD CONSTRAINT connector_cursor_target_id_fkey FOREIGN KEY (target_id) REFERENCES federation.connector_target(id) ON DELETE CASCADE;
