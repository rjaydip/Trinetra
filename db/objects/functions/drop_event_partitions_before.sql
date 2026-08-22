CREATE OR REPLACE FUNCTION federation.drop_event_partitions_before(cutoff date) RETURNS TABLE(dropped_partition text)
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    r RECORD;
BEGIN
    IF cutoff IS NULL OR cutoff > CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing retention cutoff % : it is not in the past, so it would drop the '
            'partition being written to right now.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- The regex filter is materialised BEFORE to_date runs. SQL does not guarantee predicate
    -- order, so a flat WHERE would let to_date('_default') be attempted and raise.
    FOR r IN
        WITH daily AS MATERIALIZED (
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation' AND c.relname ~ '^federation_event_[0-9]{8}$'
        )
        SELECT relname FROM daily WHERE to_date(right(relname, 8), 'YYYYMMDD') < cutoff
    LOOP
        EXECUTE format('DROP TABLE federation.%I', r.relname);
        dropped_partition := r.relname;
        RETURN NEXT;
    END LOOP;
END $function$

