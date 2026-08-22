CREATE OR REPLACE FUNCTION federation.drop_health_partitions_before(cutoff date) RETURNS TABLE(dropped_partition text)
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    r RECORD;
BEGIN
    IF cutoff IS NULL OR cutoff > CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing retention cutoff % : it is not in the past.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    FOR r IN
        WITH daily AS MATERIALIZED (
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation' AND c.relname ~ '^connector_health_[0-9]{8}$'
        )
        SELECT relname FROM daily WHERE to_date(right(relname, 8), 'YYYYMMDD') < cutoff
    LOOP
        EXECUTE format('DROP TABLE federation.%I', r.relname);
        dropped_partition := r.relname;
        RETURN NEXT;
    END LOOP;
END $function$

