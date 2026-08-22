CREATE OR REPLACE FUNCTION federation.ensure_audit_partitions(from_month date DEFAULT (date_trunc('month'::text, (CURRENT_DATE)::timestamp with time zone))::date, months_ahead integer DEFAULT 3) RETURNS integer
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    m         DATE;
    created   INTEGER := 0;
    tbl       TEXT;
    part_name TEXT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY['credential_access_log', 'config_audit'] LOOP
        FOR i IN 0..months_ahead LOOP
            m := (date_trunc('month', from_month) + (i || ' month')::interval)::date;
            part_name := format('%s_%s', tbl, to_char(m, 'YYYYMM'));

            IF NOT EXISTS (
                SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relname = part_name AND n.nspname = 'federation'
            ) THEN
                EXECUTE format(
                    'CREATE TABLE federation.%I PARTITION OF federation.%I '
                    'FOR VALUES FROM (%L) TO (%L)',
                    part_name, tbl, m, (m + interval '1 month')::date);
                created := created + 1;
            END IF;
        END LOOP;
    END LOOP;

    RETURN created;
END $function$

