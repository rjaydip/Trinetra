-- Name: worker_node; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.worker_node (
    worker_id text NOT NULL,
    hostname text NOT NULL,
    runtime_class federation.runtime_class NOT NULL,
    vendor_filter federation.vendor_kind[],
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    last_heartbeat timestamp with time zone DEFAULT now() NOT NULL,
    claimed_count integer DEFAULT 0 NOT NULL
);

--
-- Name: worker_node worker_node_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.worker_node
    ADD CONSTRAINT worker_node_pkey PRIMARY KEY (worker_id);
