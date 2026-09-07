-- ===========================================================================
-- v1.9 — auth_audit: the authentication event trail (PR7b, finding 4-H3)
-- ===========================================================================
--
-- config_audit records who changed what. It says nothing about who logged in,
-- who failed to, whose account locked, when a token was rotated or replayed,
-- or which API key authenticated from where. For an estate holding camera
-- credentials and citizen-surveillance data that is a baseline accountability
-- gap, and it is also how the brute-force activity finding 4-H5 makes possible
-- gets detected.
--
-- auth_audit is append-only, partitioned by month like config_audit, read only
-- through the new authaudit.read permission (SUPER_ADMIN only — deliberately
-- NOT bundled with config.audit.read), and retained on its own period
-- (Retention:AuthAuditMonths, default 24) separate from AuditMonths.
--
-- The login failure RESPONSE is coarse — an unknown username and a wrong
-- password for a real account both return the same 401. The TRAIL records which:
-- a resolved user_id means the name existed, a presented_username with no
-- user_id means it did not. That is deliberate — an incident investigator needs
-- the distinction, and the trail is readable only by SUPER_ADMIN, so it is not
-- an enumeration surface. API-key auth SUCCESS is sampled
-- (~one row per key per 5 minutes, paired with the coarse last_used_at in 7c);
-- API-key auth FAILURE is always recorded.
--
-- Transaction is owned by MigrationRunner: do not add BEGIN/COMMIT here.
SET search_path TO federation, public;

CREATE TABLE IF NOT EXISTS auth_audit (
    id                 BIGINT GENERATED ALWAYS AS IDENTITY,
    occurred_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    event_type         TEXT NOT NULL,   -- AuthAuditEvents.* in application code
    outcome            TEXT NOT NULL
                       CHECK (outcome IN ('success', 'failure', 'lockout', 'revoked')),

    -- ON DELETE SET NULL on both: a user or key delete must never be blocked by,
    -- or cascade into, the audit trail.
    user_id            UUID REFERENCES platform_users(id) ON DELETE SET NULL,
    api_key_id         UUID REFERENCES api_key(id) ON DELETE SET NULL,

    -- The typed-in string, only when the principal could not be resolved. A
    -- resolved user_id makes it redundant and possibly misleading.
    presented_username TEXT
                       CHECK (presented_username IS NULL OR char_length(presented_username) <= 256),
    CONSTRAINT auth_audit_username_only_when_anon
        CHECK (presented_username IS NULL OR user_id IS NULL),

    source_address     TEXT,   -- Connection.RemoteIpAddress as-is; forwarded-header
                               -- / trusted-proxy hardening is a separate work item.
    user_agent         TEXT,
    jti                TEXT,   -- refresh-token id, where the event has one
    detail             JSONB,

    PRIMARY KEY (occurred_at, id)
) PARTITION BY RANGE (occurred_at);

CREATE TABLE IF NOT EXISTS auth_audit_default PARTITION OF auth_audit DEFAULT;

CREATE INDEX IF NOT EXISTS ix_auth_audit_user
    ON auth_audit (user_id, occurred_at DESC);

-- The triage query: everything that is not a clean success.
CREATE INDEX IF NOT EXISTS ix_auth_audit_failures
    ON auth_audit (occurred_at DESC) WHERE outcome <> 'success';


-- ---------------------------------------------------------------------------
-- Partition maintenance: auth_audit joins the monthly audit tables.
-- ---------------------------------------------------------------------------
-- Body-only change (one extra array element); signature unchanged, so
-- CREATE OR REPLACE is correct.

CREATE OR REPLACE FUNCTION ensure_audit_partitions(
    from_month DATE DEFAULT date_trunc('month', CURRENT_DATE)::date,
    months_ahead INTEGER DEFAULT 3
) RETURNS INTEGER
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    m         DATE;
    created   INTEGER := 0;
    tbl       TEXT;
    part_name TEXT;
BEGIN
    FOREACH tbl IN ARRAY ARRAY['credential_access_log', 'config_audit', 'auth_audit'] LOOP
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
END $$;


-- auth_audit has its OWN retention period, so it does not match
-- drop_audit_partitions_before's '^(config_audit|credential_access_log)_...' — it
-- gets a sibling driven by Retention:AuthAuditMonths.

CREATE OR REPLACE FUNCTION drop_auth_audit_partitions_before(cutoff DATE)
RETURNS TABLE (dropped_partition TEXT)
LANGUAGE plpgsql
SET search_path = federation, public
AS $$
DECLARE
    r RECORD;
BEGIN
    IF cutoff IS NULL OR cutoff > CURRENT_DATE THEN
        RAISE EXCEPTION
            'Refusing retention cutoff % : it is not in the past.', cutoff
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    FOR r IN
        WITH monthly AS MATERIALIZED (
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'federation'
              AND c.relname ~ '^auth_audit_[0-9]{6}$'
        )
        SELECT relname FROM monthly
        WHERE (to_date(right(relname, 6), 'YYYYMM') + interval '1 month')::date <= cutoff
    LOOP
        EXECUTE format('DROP TABLE federation.%I', r.relname);
        dropped_partition := r.relname;
        RETURN NEXT;
    END LOOP;
END $$;


-- retention_status gains an auth_audit row. No column/type change.

CREATE OR REPLACE VIEW retention_status AS
WITH parts AS (
    SELECT c.relname,
           CASE
               WHEN c.relname ~ '^federation_event_[0-9]{8}$'   THEN 'federation_event'
               WHEN c.relname ~ '^connector_health_[0-9]{8}$'   THEN 'connector_health'
               WHEN c.relname ~ '^config_audit_[0-9]{6}$'       THEN 'config_audit'
               WHEN c.relname ~ '^credential_access_log_[0-9]{6}$' THEN 'credential_access_log'
               WHEN c.relname ~ '^auth_audit_[0-9]{6}$'         THEN 'auth_audit'
           END AS table_name,
           CASE
               WHEN c.relname ~ '[0-9]{8}$' THEN to_date(right(c.relname, 8), 'YYYYMMDD')
               ELSE to_date(right(c.relname, 6), 'YYYYMM')
           END AS covers_from,
           pg_total_relation_size(c.oid) AS bytes
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'federation'
      AND (c.relname ~ '^federation_event_[0-9]{8}$'
        OR c.relname ~ '^connector_health_[0-9]{8}$'
        OR c.relname ~ '^config_audit_[0-9]{6}$'
        OR c.relname ~ '^credential_access_log_[0-9]{6}$'
        OR c.relname ~ '^auth_audit_[0-9]{6}$')
)
SELECT table_name,
       count(*)              AS partitions,
       min(covers_from)      AS oldest_data,
       max(covers_from)      AS newest_partition,
       pg_size_pretty(sum(bytes)) AS total_size
FROM parts
GROUP BY table_name;


-- ---------------------------------------------------------------------------
-- Permission: read the authentication audit trail. SUPER_ADMIN only.
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('authaudit.read', 'View authentication audit trail', 'admin',
     'Read the append-only record of logins, lockouts, token rotation, password '
     || 'changes and API-key authentication outcomes')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category,
        description = EXCLUDED.description;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, 'authaudit.read'
FROM roles r
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;
