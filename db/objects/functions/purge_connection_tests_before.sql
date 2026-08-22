CREATE OR REPLACE FUNCTION federation.purge_connection_tests_before(cutoff timestamp with time zone) RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    removed INTEGER;
BEGIN
    -- Only terminal jobs. A test still pending or running is not old, however long ago it was
    -- requested -- deleting one would leave a client polling an id that no longer exists.
    DELETE FROM connection_test
    WHERE requested_at < cutoff
      AND status IN ('completed', 'failed', 'abandoned');

    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END $function$

