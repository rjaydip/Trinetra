CREATE OR REPLACE FUNCTION federation.drop_camera_status_partitions_before(cutoff date) RETURNS TABLE(dropped_partition text)
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    part   RECORD;
    bound  DATE;
BEGIN
    IF cutoff IS NULL OR cutoff >= CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing to drop camera status partitions with cutoff % : it is not in the past. '
            'That would drop the partition being written to right now.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    FOR part IN
        SELECT c.relname
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'federation'
          AND c.relname ~ '^camera_status_history_[0-9]{8}$'
    LOOP
        bound := to_date(right(part.relname, 8), 'YYYYMMDD');

        IF bound < cutoff THEN
            EXECUTE format('DROP TABLE federation.%I', part.relname);
            dropped_partition := part.relname;
            RETURN NEXT;
        END IF;
    END LOOP;
END $function$
