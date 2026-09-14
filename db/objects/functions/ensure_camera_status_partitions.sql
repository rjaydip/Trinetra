CREATE OR REPLACE FUNCTION federation.ensure_camera_status_partitions(from_date date DEFAULT CURRENT_DATE, days_ahead integer DEFAULT 14) RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    d         DATE;
    part_name TEXT;
    created   INTEGER := 0;
BEGIN
    FOR i IN 0..days_ahead LOOP
        d := from_date + i;
        part_name := format('camera_status_history_%s', to_char(d, 'YYYYMMDD'));

        IF NOT EXISTS (
            SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = part_name AND n.nspname = 'federation'
        ) THEN
            BEGIN
                EXECUTE format(
                    'CREATE TABLE federation.%I PARTITION OF federation.camera_status_history '
                    'FOR VALUES FROM (%L) TO (%L)', part_name, d, d + 1);
                created := created + 1;
            EXCEPTION WHEN check_violation THEN
                RAISE EXCEPTION
                    'Cannot create partition % : camera_status_history_default holds rows in '
                    'range [%, %). Drain those rows into their correct partition first.',
                    part_name, d, d + 1
                    USING ERRCODE = 'check_violation';
            END;
        END IF;
    END LOOP;

    RETURN created;
END $function$
