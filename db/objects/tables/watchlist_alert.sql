--
-- Name: watchlist_alert; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.2. Raised when an ingested detection_event matches an active watchlist_entry,
-- inside the ingest transaction. One alert per (entry, detection).
--

CREATE TABLE federation.watchlist_alert (
    id uuid DEFAULT gen_random_uuid() NOT NULL,
    watchlist_entry_id uuid NOT NULL,
    detection_event_id text NOT NULL,
    detection_occurred_at timestamp with time zone NOT NULL,
    raised_at timestamp with time zone DEFAULT now() NOT NULL,
    acknowledged_at timestamp with time zone,
    acknowledged_by uuid
);

ALTER TABLE ONLY federation.watchlist_alert
    ADD CONSTRAINT watchlist_alert_pkey PRIMARY KEY (id);

CREATE UNIQUE INDEX ux_watchlist_alert_dedup ON federation.watchlist_alert USING btree (watchlist_entry_id, detection_event_id);
CREATE INDEX ix_watchlist_alert_raised ON federation.watchlist_alert USING btree (raised_at DESC);

ALTER TABLE ONLY federation.watchlist_alert
    ADD CONSTRAINT watchlist_alert_watchlist_entry_id_fkey FOREIGN KEY (watchlist_entry_id) REFERENCES federation.watchlist_entry(id) ON DELETE CASCADE;
ALTER TABLE ONLY federation.watchlist_alert
    ADD CONSTRAINT watchlist_alert_detection_fkey FOREIGN KEY (detection_occurred_at, detection_event_id) REFERENCES federation.detection_event(occurred_at, event_id) ON DELETE CASCADE;
ALTER TABLE ONLY federation.watchlist_alert
    ADD CONSTRAINT watchlist_alert_acknowledged_by_fkey FOREIGN KEY (acknowledged_by) REFERENCES federation.platform_users(id);
