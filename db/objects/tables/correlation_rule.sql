-- Name: correlation_rule; Type: TABLE; Schema: federation; Owner: -
--

CREATE TABLE federation.correlation_rule (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    code text NOT NULL,
    name text NOT NULL,
    description text,
    time_window_seconds integer NOT NULL,
    spatial_radius_meters double precision,
    confidence_floor double precision NOT NULL,
    required_signal_agreement integer DEFAULT 2 NOT NULL,
    is_active boolean DEFAULT true NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT correlation_rule_confidence_floor_check CHECK (((confidence_floor >= (0)::double precision) AND (confidence_floor <= (1)::double precision))),
    CONSTRAINT correlation_rule_required_signal_agreement_check CHECK ((required_signal_agreement >= 2)),
    CONSTRAINT correlation_rule_spatial_radius_meters_check CHECK (((spatial_radius_meters IS NULL) OR (spatial_radius_meters > (0)::double precision))),
    CONSTRAINT correlation_rule_time_window_seconds_check CHECK ((time_window_seconds > 0))
);

COMMENT ON TABLE federation.correlation_rule IS 'Per-rule correlation config: time window, spatial radius, confidence floor, required signal agreement (architecture §8). System-seeded; no write API in this pass -- edited by a future admin route, not by a deployment.';

--
-- Name: correlation_rule correlation_rule_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.correlation_rule
    ADD CONSTRAINT correlation_rule_pkey PRIMARY KEY (id);

--
-- Name: correlation_rule correlation_rule_code_key; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.correlation_rule
    ADD CONSTRAINT correlation_rule_code_key UNIQUE (code);
