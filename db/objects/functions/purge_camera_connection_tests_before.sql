CREATE OR REPLACE FUNCTION federation.purge_camera_connection_tests_before(cutoff timestamp with time zone) RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    removed INTEGER;
BEGIN
    DELETE FROM camera_connection_test
    WHERE requested_at < cutoff
      AND status IN ('completed', 'failed', 'abandoned');

    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END $function$
