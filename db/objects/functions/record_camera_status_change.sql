--
-- Name: record_camera_status_change(); Type: FUNCTION; Schema: federation; Owner: -
--
-- Feeds camera_status_history from federated_camera. Since v1.11 the row carries
-- geographic_area_id directly (sites removed) instead of site_id.

CREATE OR REPLACE FUNCTION federation.record_camera_status_change() RETURNS trigger
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
BEGIN
    INSERT INTO camera_status_history (
        target_id, native_camera_id, changed_at,
        organization_unit_id, geographic_area_id,
        previous_health, health,
        previous_enabled, is_enabled,
        previous_recording, is_recording)
    VALUES (
        NEW.target_id, NEW.native_camera_id, NEW.updated_at,
        NEW.organization_unit_id, NEW.geographic_area_id,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.health       END, NEW.health,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.is_enabled   END, NEW.is_enabled,
        CASE WHEN TG_OP = 'UPDATE' THEN OLD.is_recording END, NEW.is_recording)
    ON CONFLICT DO NOTHING;

    RETURN NULL;
END $function$
