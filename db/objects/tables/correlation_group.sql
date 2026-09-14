-- Name: correlation_group; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.correlation_group (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    rule_id uuid NOT NULL,
    natural_key text NOT NULL,
    window_bucket timestamp with time zone NOT NULL,
    confidence double precision NOT NULL,
    member_count integer NOT NULL,
    first_occurred_at timestamp with time zone NOT NULL,
    last_occurred_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT correlation_group_confidence_check CHECK (((confidence >= (0)::double precision) AND (confidence <= (1)::double precision))),
    CONSTRAINT correlation_group_member_count_check CHECK ((member_count >= 2))
);

COMMENT ON TABLE federation.correlation_group IS 'One cluster of events an ICorrelationEngine run judged a possible match. confidence is always a possible-match score, never certainty. Deduplicated on (rule_id, natural_key, window_bucket), not on id.';

--
-- Name: correlation_group correlation_group_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.correlation_group
    ADD CONSTRAINT correlation_group_pkey PRIMARY KEY (id);

--
-- Name: correlation_group uq_correlation_group_dedup; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.correlation_group
    ADD CONSTRAINT uq_correlation_group_dedup UNIQUE (rule_id, natural_key, window_bucket);

--
-- Name: correlation_group correlation_group_rule_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.correlation_group
    ADD CONSTRAINT correlation_group_rule_id_fkey FOREIGN KEY (rule_id) REFERENCES federation.correlation_rule(id);

--
-- Name: ix_correlation_group_last_occurred; Type: INDEX; Schema: federation; Owner: -
--

CREATE INDEX ix_correlation_group_last_occurred ON federation.correlation_group USING btree (last_occurred_at DESC);
