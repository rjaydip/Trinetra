CREATE OR REPLACE VIEW federation.retention_status AS
 WITH parts AS (
         SELECT c.relname,
                CASE
                    WHEN c.relname ~ '^federation_event_[0-9]{8}$'::text THEN 'federation_event'::text
                    WHEN c.relname ~ '^connector_health_[0-9]{8}$'::text THEN 'connector_health'::text
                    WHEN c.relname ~ '^config_audit_[0-9]{6}$'::text THEN 'config_audit'::text
                    WHEN c.relname ~ '^credential_access_log_[0-9]{6}$'::text THEN 'credential_access_log'::text
                    ELSE NULL::text
                END AS table_name,
                CASE
                    WHEN c.relname ~ '[0-9]{8}$'::text THEN to_date("right"(c.relname::text, 8), 'YYYYMMDD'::text)
                    ELSE to_date("right"(c.relname::text, 6), 'YYYYMM'::text)
                END AS covers_from,
            pg_total_relation_size(c.oid::regclass) AS bytes
           FROM pg_class c
             JOIN pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspname = 'federation'::name AND (c.relname ~ '^federation_event_[0-9]{8}$'::text OR c.relname ~ '^connector_health_[0-9]{8}$'::text OR c.relname ~ '^config_audit_[0-9]{6}$'::text OR c.relname ~ '^credential_access_log_[0-9]{6}$'::text)
        )
 SELECT table_name,
    count(*) AS partitions,
    min(covers_from) AS oldest_data,
    max(covers_from) AS newest_partition,
    pg_size_pretty(sum(bytes)) AS total_size
   FROM parts
  GROUP BY table_name;
