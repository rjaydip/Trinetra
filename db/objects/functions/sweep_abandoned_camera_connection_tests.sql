CREATE OR REPLACE FUNCTION federation.sweep_abandoned_camera_connection_tests(stale_after interval DEFAULT '00:05:00'::interval) RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    swept INTEGER;
BEGIN
    UPDATE camera_connection_test
    SET status = 'abandoned',
        completed_at = now(),
        failure_reason = format(
            'No result after %s. The API instance running this test most likely stopped.',
            stale_after)
    WHERE status IN ('pending', 'running')
      AND COALESCE(started_at, requested_at) < now() - stale_after;

    GET DIAGNOSTICS swept = ROW_COUNT;
    RETURN swept;
END $function$
