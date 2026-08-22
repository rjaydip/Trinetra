CREATE OR REPLACE FUNCTION federation.principal_groups(p_user_id uuid, p_api_key_id uuid) RETURNS TABLE(group_id uuid)
 LANGUAGE sql
 STABLE
 SET search_path TO 'federation', 'public'
AS $function$
    -- Human principal: membership must be active and unexpired (RBAC section 26).
    SELECT ag.id
    FROM platform_users u
    JOIN user_groups ug   ON ug.user_id = u.id
                         AND ug.status = 'ACTIVE'
                         AND (ug.expires_at IS NULL OR ug.expires_at > now())
    JOIN access_groups ag ON ag.id = ug.group_id AND ag.status = 'ACTIVE'
    WHERE p_api_key_id IS NULL
      AND u.id = p_user_id
      AND u.status = 'ACTIVE'

    UNION

    -- Machine principal: the key must not be revoked or expired. Checked here as well as at
    -- authentication, so a key revoked mid-request cannot finish the request it started.
    SELECT ag.id
    FROM api_key k
    JOIN access_groups ag ON ag.id = k.group_id AND ag.status = 'ACTIVE'
    WHERE p_user_id IS NULL
      AND k.id = p_api_key_id
      AND k.revoked_at IS NULL
      AND (k.expires_at IS NULL OR k.expires_at > now());
$function$

