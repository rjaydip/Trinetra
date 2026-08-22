CREATE OR REPLACE VIEW federation.event_partition_health AS
 SELECT ( SELECT count(*) AS count
           FROM federation.federation_event_default) AS rows_in_default_partition,
    ( SELECT count(*) AS count
           FROM pg_class c
             JOIN pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspname = 'federation'::name AND c.relname ~ '^federation_event_[0-9]{8}$'::text) AS daily_partition_count,
    ( SELECT max(to_date("right"(daily.relname::text, 8), 'YYYYMMDD'::text)) AS max
           FROM ( SELECT c.relname
                   FROM pg_class c
                     JOIN pg_namespace n ON n.oid = c.relnamespace
                  WHERE n.nspname = 'federation'::name AND c.relname ~ '^federation_event_[0-9]{8}$'::text) daily) AS furthest_partition_date;
