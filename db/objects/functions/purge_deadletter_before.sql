CREATE OR REPLACE FUNCTION federation.purge_deadletter_before(cutoff timestamp with time zone) RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    removed INTEGER;
BEGIN
    DELETE FROM federation_event_deadletter WHERE received_at < cutoff;
    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END $function$

