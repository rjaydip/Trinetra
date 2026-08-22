-- Name: maintenance_run; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.maintenance_run (
    job text NOT NULL,
    last_run_date date NOT NULL,
    last_run_at timestamp with time zone DEFAULT now() NOT NULL,
    detail text
);

--
-- Name: maintenance_run maintenance_run_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.maintenance_run
    ADD CONSTRAINT maintenance_run_pkey PRIMARY KEY (job);
