-- Name: video_wall_preference; Type: TABLE; Schema: federation; Owner: -
-- Added: v1.20 (per-user video-wall layout preference)
--

CREATE TABLE federation.video_wall_preference (
    user_id uuid NOT NULL,
    tile_count integer NOT NULL,
    column_count integer DEFAULT 2 NOT NULL,
    camera_ids uuid[] DEFAULT '{}'::uuid[] NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT video_wall_preference_column_count_check CHECK (((column_count >= 1) AND (column_count <= 12))),
    CONSTRAINT video_wall_preference_tile_count_check CHECK (((tile_count >= 1) AND (tile_count <= 64)))
);

COMMENT ON TABLE federation.video_wall_preference IS 'A caller''s own video-wall layout -- descriptive UI preference, no organization/geography scope, no permission gate beyond authentication, no audit row. camera_ids carries the wall in grid order; tile_count tracks its length as a read-side convenience, always cameraIds.length on write.';

--
-- Name: video_wall_preference video_wall_preference_pkey; Type: CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.video_wall_preference
    ADD CONSTRAINT video_wall_preference_pkey PRIMARY KEY (user_id);

--
-- Name: video_wall_preference video_wall_preference_user_id_fkey; Type: FK CONSTRAINT; Schema: federation; Owner: -
--

ALTER TABLE ONLY federation.video_wall_preference
    ADD CONSTRAINT video_wall_preference_user_id_fkey FOREIGN KEY (user_id) REFERENCES federation.platform_users(id) ON DELETE CASCADE;
