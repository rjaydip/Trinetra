-- ===========================================================================
-- v1.13 — AI-worker heartbeat hardening (API review Finding 17: M1, M2, M3, L1, L2)
-- ===========================================================================
--
-- The worker-health heartbeat (POST /api/v1/worker-health/heartbeat) trusted a
-- client-supplied worker id and a client-supplied timestamp, took no caller
-- context, and shared one permission between "submit a heartbeat" and "read the
-- whole fleet inventory". For a monitoring signal that is too loose:
--
--   17-M1 — anyone holding worker.heartbeat could POST as any worker id and keep
--           a dead process looking alive, or flood fake ids to bury a real
--           outage. Ownership is now bound to the API key that reports:
--           ai_worker_health.api_key_id is NOT NULL and part of the identity, so
--           a heartbeat can only ever touch a row under the caller's own key.
--
--   17-M2 — last_heartbeat_at was set from the client clock; a future-dated
--           value made a worker look alive forever. It is now written
--           server-side (application passes now via the repository); the
--           client's claimed time is kept only as reported_at, for drift
--           diagnosis (CLAUDE.md #6 — clocks drift, so keep both, trust ours).
--
--   17-M3 — one permission gated both routes. Split into worker.read (see the
--           fleet) and worker.heartbeat (submit only). STATE_ADMIN loses
--           worker.heartbeat (a human admin submits no heartbeats — v1.4
--           copy-paste) and gains worker.read. New worker.manage gates the
--           retire route below.
--
--   17-L1 — no way to clear a decommissioned worker: after 17-M3 a human admin
--           can see a stale row but not remove it, and a fleet resize
--           (WORKER_COUNT 4 -> 2) leaves ai-worker-2-of-4 / -3-of-4 stale
--           forever, read by every consumer as a crash. New
--           DELETE /api/v1/worker-health/{id} (worker.manage), audited.
--
--   17-L2 — over-long / bad-charset worker id or hostname 500'd (raw 22001).
--           Validated in the endpoint now; the column caps below are the
--           backstop.
--
-- FRESH DATA. ai_worker_health is dropped and recreated rather than altered:
-- the API is deployed nowhere, the heartbeat push is still a no-op (it only
-- runs once ai-worker's BACKEND_HEARTBEAT_URL is set), the rows are ephemeral
-- observability with no audit or business meaning and every worker re-registers
-- within one interval, and no foreign key references the table. An ALTER adding
-- a NOT NULL owner column to existing rows would need a fabricated key id
-- anyway.
--
-- Requires: v1.sql .. v1.12.sql
-- ===========================================================================

SET search_path = federation, public;

-- ---------------------------------------------------------------------------
-- ai_worker_health — rebuilt with an owning key in the identity
-- ---------------------------------------------------------------------------

DROP TABLE IF EXISTS ai_worker_health;

CREATE TABLE ai_worker_health (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),

    -- The API key that reported this heartbeat. Part of the worker's identity,
    -- not just metadata: a heartbeat upsert matches on (api_key_id, worker_id,
    -- hostname), so one integration can never write or overwrite another's
    -- liveness. ON DELETE CASCADE purges a deleted key's rows; a *revoked* key
    -- keeps revoked_at rather than being deleted, so its rows linger until the
    -- retire route (17-L1) or a future pruning job (17-L4) clears them.
    api_key_id          UUID NOT NULL REFERENCES api_key(id) ON DELETE CASCADE,

    -- The worker's own label for itself (ai-worker's WORKER_INDEX-of-COUNT).
    -- Honest client value, scoped to the reporting key's namespace; ownership
    -- is carried by api_key_id, never by parsing this string.
    worker_id           VARCHAR(128) NOT NULL,

    -- Included in the identity so two hosts accidentally running the same
    -- WORKER_INDEX under one key show as two rows (the useful signal) instead
    -- of silently refreshing one merged row.
    hostname            VARCHAR(253) NOT NULL,

    first_seen_at       TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Server-authoritative. The application always writes now() here; a client
    -- timestamp never reaches this column.
    last_heartbeat_at   TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- The client's claimed send time. Advisory only — never used in staleness
    -- logic, kept so (last_heartbeat_at - reported_at) exposes clock drift.
    reported_at         TIMESTAMPTZ,

    -- Leading column api_key_id also serves the FK cascade-delete lookup and any
    -- "WHERE api_key_id = ..." — no separate single-column index needed.
    CONSTRAINT uq_ai_worker_identity UNIQUE (api_key_id, worker_id, hostname)
);

COMMENT ON TABLE ai_worker_health IS
    'Liveness registry for Model 2 AI workers (POST /api/v1/worker-health/heartbeat). '
    'Identity is (api_key_id, worker_id, hostname): a heartbeat is bound to the reporting key '
    '(Finding 17-M1). last_heartbeat_at is server-set; reported_at is the advisory client clock '
    '(17-M2). Not worker_node — an AI worker owns a static camera partition, not a lease.';

-- ---------------------------------------------------------------------------
-- Permissions — split the one worker.heartbeat permission
-- ---------------------------------------------------------------------------

INSERT INTO permissions (code, name, category, description) VALUES
    ('worker.read',   'View AI-worker fleet', 'vms',
        'List AI-worker processes and their last heartbeat'),
    ('worker.manage', 'Retire AI-worker records', 'vms',
        'Delete a stale or decommissioned AI-worker health record')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, category = EXCLUDED.category, description = EXCLUDED.description;

-- SUPER_ADMIN holds every permission, including any added since v1.sql — the
-- CROSS JOIN there ran before these existed. Unconditional, idempotent, the
-- same backfill v1.4 / v1.11 / v1.12 use.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r CROSS JOIN permissions p
WHERE r.code = 'SUPER_ADMIN'
ON CONFLICT DO NOTHING;

-- The human admin roles that watch the fleet. Explicit list, not "every role
-- that holds worker.heartbeat" — that set includes DETECTION_WORKER, and the
-- machine worker reading the fleet inventory is exactly the target list for a
-- 17-M1 spoofing attack. STATE_ADMIN and DEPARTMENT_ADMIN administer workers;
-- only STATE_ADMIN retires records.
INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code
FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (
    ('STATE_ADMIN',      'worker.read'),
    ('STATE_ADMIN',      'worker.manage'),
    ('DEPARTMENT_ADMIN', 'worker.read')
)
ON CONFLICT DO NOTHING;

-- STATE_ADMIN loses worker.heartbeat (v1.4 gave it by copy-paste alongside the
-- read side of the detection demo path). A human admin submits no heartbeats;
-- DETECTION_WORKER keeps it and stays the only role that carries it. A plain
-- grant delete — no customized_at guard, that concern is only for a future
-- re-seed of a preset's whole composition.
DELETE FROM role_permissions rp
USING roles r
WHERE rp.role_id = r.id
  AND r.code = 'STATE_ADMIN'
  AND rp.permission_code = 'worker.heartbeat';
