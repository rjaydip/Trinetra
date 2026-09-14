CREATE OR REPLACE FUNCTION federation.stamp_camera_status_changed_at() RETURNS trigger
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
BEGIN
    NEW.status_changed_at := NEW.updated_at;
    RETURN NEW;
END $function$
