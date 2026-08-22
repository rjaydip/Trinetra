CREATE OR REPLACE FUNCTION federation.record_job_detail(p_job text, p_detail text) RETURNS void
 LANGUAGE sql
 SET search_path TO 'federation', 'public'
AS $function$
    UPDATE maintenance_run SET detail = p_detail WHERE job = p_job;
$function$

