-- Name: connector_health; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.connector_health (
    target_id uuid NOT NULL,
    checked_at timestamp with time zone NOT NULL,
    status federation.health_status NOT NULL,
    latency_ms double precision,
    camera_count integer,
    consecutive_failures integer DEFAULT 0 NOT NULL,
    circuit_open boolean DEFAULT false NOT NULL,
    last_error text,
    events_since_check bigint DEFAULT 0 NOT NULL,
    cursor_lag_seconds double precision
)
PARTITION BY RANGE (checked_at);

--
-- Name: connector_health connector_health_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.connector_health
    ADD CONSTRAINT connector_health_pkey PRIMARY KEY (target_id, checked_at);

--
-- Name: connector_health connector_health_target_id_fkey1; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE federation.connector_health
    ADD CONSTRAINT connector_health_target_id_fkey1 FOREIGN KEY (target_id) REFERENCES federation.connector_target(id) ON DELETE CASCADE;
