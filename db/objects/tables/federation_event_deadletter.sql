-- Name: federation_event_deadletter; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.federation_event_deadletter (
    id bigint NOT NULL,
    target_id uuid NOT NULL,
    received_at timestamp with time zone DEFAULT now() NOT NULL,
    reason text NOT NULL,
    raw_reference text,
    raw_payload jsonb
);

--
-- Name: federation_event_deadletter_id_seq; Type: SEQUENCE; Schema: federation; Owner: -
--

ALTER TABLE federation.federation_event_deadletter ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME federation.federation_event_deadletter_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);

--
-- Name: federation_event_deadletter federation_event_deadletter_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.federation_event_deadletter
    ADD CONSTRAINT federation_event_deadletter_pkey PRIMARY KEY (id);
