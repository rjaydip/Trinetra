CREATE OR REPLACE FUNCTION federation.try_claim_daily_job(p_job text, p_today date) RETURNS boolean
 LANGUAGE plpgsql
 SET search_path TO 'federation', 'public'
AS $function$
DECLARE
    claimed BOOLEAN;
BEGIN
    INSERT INTO maintenance_run (job, last_run_date)
    VALUES (p_job, p_today)
    ON CONFLICT (job) DO UPDATE
        SET last_run_date = EXCLUDED.last_run_date,
            last_run_at   = now()
        WHERE maintenance_run.last_run_date < EXCLUDED.last_run_date
    RETURNING TRUE INTO claimed;

    RETURN COALESCE(claimed, FALSE);
END $function$

