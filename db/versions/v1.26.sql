-- ===========================================================================
-- v1.26 — operator/free-form tagging on detections
-- ===========================================================================
--
-- RFP Model 2 "event tagging": `detection_event.event_type` is deliberately a closed set
-- (ANPR_DETECTED | VEHICLE_DETECTED — finding 15-L2, a free-text classification search and
-- dashboards can't reason about). That closed set is the AI worker's own machine classification
-- and stays exactly as-is; this is a separate, additive concept — free-form labels a human
-- operator attaches during review/triage ("suspicious", "false positive", "priority-follow-up",
-- anything they want), independent of what the detection was machine-classified as.
--
-- A normal table, not a partition of detection_event: tag volume is a small fraction of
-- detection volume (most detections are never tagged), and every read/write here is by a human
-- in a UI, not a high-throughput ingest path — none of the reasons detection_event itself is
-- partitioned by time apply to its tags.
--
-- No FK to detection_event: its primary key is (occurred_at, event_id) — occurred_at is the
-- partition key, so a normal FK "just referencing event_id" isn't expressible, and requiring
-- every tag write to carry occurred_at just to satisfy a constraint reintroduces exactly the
-- coupling denormalisation avoids elsewhere on this row (see detection_event's own
-- organization_unit_id/geographic_area_id comments). Existence + scope are instead checked by
-- the endpoint reading detection_event directly before writing a tag (DetectionTagEndpoints).
--
-- organization_unit_id/geographic_area_id are denormalised onto the tag row at write time, the
-- same convention detection_event itself already uses ("scoping a query must not join to resolve
-- ownership") — a tag list/delete never has to re-join detection_event to enforce scope.
--
-- Requires: v1.sql .. v1.25.sql
-- ===========================================================================

SET search_path = federation, public;

CREATE TABLE IF NOT EXISTS detection_tag (
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    detection_event_id    TEXT NOT NULL,
    detection_occurred_at TIMESTAMPTZ NOT NULL,

    tag                   TEXT NOT NULL CHECK (char_length(tag) BETWEEN 1 AND 40),

    -- Denormalised from detection_event at write time — see the note above.
    organization_unit_id  UUID NOT NULL REFERENCES organization_units(id),
    geographic_area_id    UUID,

    created_by            UUID REFERENCES platform_users(id),
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Case-insensitive dedup: "Reviewed" and "reviewed" are the same tag. ON CONFLICT targets this
-- expression index directly (DetectionTagRepository.AddTagAsync).
CREATE UNIQUE INDEX IF NOT EXISTS ux_detection_tag_dedup
    ON detection_tag (detection_event_id, lower(tag));

CREATE INDEX IF NOT EXISTS ix_detection_tag_event
    ON detection_tag (detection_event_id);
CREATE INDEX IF NOT EXISTS ix_detection_tag_org
    ON detection_tag (organization_unit_id);
