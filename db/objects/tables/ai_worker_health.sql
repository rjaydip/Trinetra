--
-- Name: ai_worker_health; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.2. Liveness registry for Model 2 AI workers (POST /api/v1/worker-health/heartbeat).
-- Not worker_node: an AI worker owns a static camera partition, not a connector-target lease.
--

CREATE TABLE federation.ai_worker_health (
    worker_id text NOT NULL,
    hostname text,
    first_seen_at timestamp with time zone DEFAULT now() NOT NULL,
    last_heartbeat_at timestamp with time zone DEFAULT now() NOT NULL
);

ALTER TABLE ONLY federation.ai_worker_health
    ADD CONSTRAINT ai_worker_health_pkey PRIMARY KEY (worker_id);
