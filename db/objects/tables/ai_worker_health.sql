--
-- Name: ai_worker_health; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.2. Rebuilt: v1.13 (Finding 17-M1/M2 — heartbeat bound to the reporting
-- API key; last_heartbeat_at server-set, reported_at is the advisory client clock).
-- Liveness registry for Model 2 AI workers (POST /api/v1/worker-health/heartbeat).
-- Not worker_node: an AI worker owns a static camera partition, not a connector-target lease.
--

CREATE TABLE federation.ai_worker_health (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    api_key_id uuid NOT NULL,
    worker_id character varying(128) NOT NULL,
    hostname character varying(253) NOT NULL,
    first_seen_at timestamp with time zone DEFAULT now() NOT NULL,
    last_heartbeat_at timestamp with time zone DEFAULT now() NOT NULL,
    reported_at timestamp with time zone
);

ALTER TABLE ONLY federation.ai_worker_health
    ADD CONSTRAINT ai_worker_health_pkey PRIMARY KEY (id);

ALTER TABLE ONLY federation.ai_worker_health
    ADD CONSTRAINT uq_ai_worker_identity UNIQUE (api_key_id, worker_id, hostname);

ALTER TABLE ONLY federation.ai_worker_health
    ADD CONSTRAINT ai_worker_health_api_key_id_fkey
    FOREIGN KEY (api_key_id) REFERENCES federation.api_key(id) ON DELETE CASCADE;

-- No separate api_key_id index: uq_ai_worker_identity's btree leads with api_key_id.
