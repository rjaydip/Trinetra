# Operations — Model 3

How to prepare a database, configure a deployment, start the services, and upgrade them.

`ARCHITECTURE-MODEL-3.md` explains *why* the system is shaped this way. This document is what
you follow to run it. `DEPLOYMENT.md` covers installing it across real hosts — topology, systemd
units, and rolling upgrades.

---

## 1. The order that matters

```text
   selected database provider     database is reachable
            |
            v
   apply db/versions/v1.sql      create the schema
            |
            v
   edit config/trinetra.settings.json      or set the equivalent environment variables
            |
            v
   dotnet run --project src/Trinetra.Federation.Api
   dotnet run --project src/Trinetra.Federation.Worker
```

**Nothing in the running system touches the schema.** The services do not create it, change it,
or check it — they assume it is there. The database is entirely yours to prepare.

The cost of that is worth stating: a service pointed at an unprepared or out-of-date database
will start, report healthy, and fail requests with `relation ... does not exist` — which reads as
a code fault rather than a missing setup step. Nothing will warn you.

The database provider is intentionally not fixed by the documentation and may change between
environments. The current development environment uses Supabase-hosted PostgreSQL. Supabase is
only the current provider, not an application requirement. Set `ConnectionStrings:Federation` (or
`ConnectionStrings__Federation`) to the selected PostgreSQL provider and apply the schema there.

### Versions

```text
   db/versions/v1.sql       everything for v1 — tables, functions, views, reference data
   db/versions/v1.1.sql     only what v1.1 adds, applied on top of a v1 database
   ...
   db/versions/v1.6.sql     camera registry, health & maintenance tables, and the
                            camera.delete / camera.import / camera.reconcile permissions
```

The latest version is `v1.6`. A database that stopped earlier will 403 on the camera-registry
routes that need the new permissions, and 500 (`relation "cameras" does not exist`) on the
registry and GIS endpoints.

For a **fresh** database, `db/full-schema.sql` is a single self-contained script — every version
file inlined in apply order, including the reference data (permissions, roles) — so one command
builds the whole database:

```text
psql -U trinetra -d trinetra -f db/full-schema.sql
```

`db/versions/*.sql` stays the source of truth; regenerate `full-schema.sql` after adding a
version file. Do not run it against a database that already has an earlier version — apply only
the individual files it has not had. It does not include `db/seed/dev-sample-data.sql`.

A fresh install applies them in order. An existing database applies only what it has not had.
**A version file is never edited once applied anywhere** — installs that ran the old text would
silently differ from those that ran the new one. Corrections go in the next version.

---

## 2. First-time setup

### Prerequisites

| Requirement | Notes |
|---|---|
| PostgreSQL 17 | Stock. **No extensions** — coordinates are plain `DECIMAL`, and `gen_random_uuid()` is core from PostgreSQL 13 |
| `psql` | Taken from the host if present, otherwise from inside the running container |

### Prepare the database

The repository includes Docker Compose as an optional local PostgreSQL fallback. Skip it when
using Supabase or another PostgreSQL provider. Apply `db/versions/v1.sql` through the selected
provider's SQL client or with `psql`, using that provider's host, port, database, and credentials.

```bash
# Optional local fallback only.
docker compose up -d

# Example; replace the placeholders with the selected provider's connection details.
psql -h your-database-host -U your-database-user -d trinetra -f db/versions/v1.sql
```

For the current Supabase development environment, the same schema can be applied through the
Supabase SQL editor or a `psql` connection created from the Supabase project settings.

The file creates everything: schema, tables, functions, views, and the reference data
(permissions, roles, role_permissions) that authorisation resolves against. A schema-only script
would apply cleanly and then authorise nobody.



---

## 3. Configuration

One file, `config/trinetra.settings.json`, is linked into the API, the worker and the CLI. Three
copies would drift, and the failure would be silent: a CLI whose encryption key differed from the
API's would simply be unable to read credentials the API wrote, reporting corruption rather than
misconfiguration.

Every value can be overridden by an environment variable, which takes precedence. A double
underscore is a nested key: `Auth__Jwt__SigningKey` sets `Auth:Jwt:SigningKey`.

### What you must set

| Setting | How to produce it | What happens if it is wrong |
|---|---|---|
| `ConnectionStrings:Federation` | Host, port, database, credentials | Startup fails immediately |
| `Secrets:Key` | `openssl rand -base64 32` | See the warning below |
| `Auth:Jwt:SigningKeys` | An ordered list, `openssl rand -base64 48` per entry. First entry signs, all entries validate. The legacy scalar `Auth:Jwt:SigningKey` is still accepted as a one-key ring (`kid` `legacy`). | Startup refuses rather than run with a guessable key. A leaked key lets anyone forge a session as any user |
| `Auth:SeedAdmin:Password` | A strong value you will rotate at first login | Startup refuses an empty or short value rather than create a weak administrator |
| `Auth:AllowedOrigins` | Where the frontend is served from | Never a wildcard. Requests carry credentials, so a wildcard lets any site an operator visits act as them |

> **`Secrets:Key` must never change once credentials are stored.** Every stored camera password
> becomes permanently unreadable and must be re-entered by hand across the whole estate.
> `Secrets:KeyId` records which key sealed each secret, so rotation can be staged rather than
> done as a flag day.

#### Rotating the JWT signing key

Restart-based — there is no hot reload and no JWKS endpoint, matching the schema posture:
operational discipline, not runtime enforcement.

1. Generate a key: `openssl rand -base64 48`.
2. Add it as a **non-first** entry in `Auth:Jwt:SigningKeys` on every host, then restart the
   fleet — a rolling restart is safe, mixed old/new signers all validate both keys. Every host
   now validates tokens signed with either key; all hosts still sign with the old first entry.
3. Confirm every host has actually restarted with the new config (deploy tooling, or
   `systemctl show trinetra-api -p ActiveEnterTimestamp`), then move the new entry to **first**
   on every host and restart again. New tokens are signed with the new key; tokens signed with
   the old key still validate until they expire (≤ one access lifetime, 15 min).
4. A cycle later — after every old access token has expired — drop the old entry and restart.

Refresh-token sessions are opaque database rows, not signed with this key, so **no logged-in
user is signed out by a rotation** — only in-flight access tokens age out over ~15 minutes.
Never remove a key that is still first, and never remove the only key. Adding a key straight as
first (skipping step 2) breaks every access token the instant the first host restarts, forcing
an estate-wide refresh.

The three secrets should come from the environment in any real deployment. The committed file
documents the shape, not the values — and in Production the API **refuses to start** if any of
them still matches what is committed. That check exists because the settings file ships next to
the binaries: miss one line in the environment file and the service starts, reports healthy, and
runs on a key that is in the repository, with no runtime symptom at all.

### Retention

```jsonc
"Retention": {
  "Enabled": true,
  "RunAtUtcHour": 3,
  "EventDays": 90,
  "HealthDays": 30,
  "CameraStatusDays": 90,
  "AuditMonths": 84,
  "AuthAuditMonths": 24,
  "ConnectionTestDays": 30,
  "DeadLetterDays": 90
}
```

`AuthAuditMonths` is the authentication audit trail's own period (default 24), separate from
`AuditMonths` — the auth trail is smaller and its lawful retention is a different question from
configuration-change history. Startup refuses a value below one month; `auth_audit` appears in
`federation.retention_status` alongside the other partitioned tables.

### Detection evidence

```jsonc
"Evidence": {
  "RootPath": "evidence",         // where decoded snapshots are written; default "evidence"
  "MaxSnapshotBytes": 4194304     // per-snapshot cap; default 4 MiB
}
```

`POST /api/v1/detections` caps the whole request body at 8 MiB and `evidence.snapshotBase64`
at `MaxSnapshotBytes` — an oversized or malformed value is a `400`, decided on the encoded
string length before anything is allocated for the decode. Both have working code defaults, so
neither key must be set.

The daily maintenance pass runs `federation.ensure_audit_partitions()` before it drops anything,
so partitions for the coming months always exist. It is **not optional**: with `Enabled: false`,
or if the pass never runs, every audit and event row lands in the `*_default` partition, which
retention never reclaims — `federation.retention_status` and `federation.event_partition_health`
are how you check that partitions are being created on schedule.

**These are a policy decision, not a technical one.** CCTV event metadata is subject to statutory
retention rules that differ by state, by department and by the purpose the cameras serve. The
defaults are sized for disk, not for law. Confirm them against the retention policy the
deployment is bound by before going live.

Startup refuses any period below **7 days**. The realistic mistake is a misplaced digit, and
retention drops partitions — immediate, irreversible, no recycle bin.

`Enabled: false` exists so deletion can be paused during an investigation. It is not a safe
long-term setting: `federation_event` takes 100–400M rows/day, so leaving it off is a disk-full
outage on a schedule.

`CameraStatusDays` is longer than `HealthDays` on purpose. It governs `camera_status_history`,
which stores **only genuine status transitions** — a camera healthy for a month is one row, not a
month of samples — so 90 days costs very little, and "when did that junction camera go
Unreachable?" is a question asked weeks after the fact. Dropping those partitions never affects
what the camera's status *is now*: `federated_camera.status_changed_at` carries the current
status and the instant it began, and is not subject to retention.

### Network

```jsonc
"Network": {
  "TrustForwardedHeaders": false,
  "KnownProxies": [],
  "KnownNetworks": []
}
```

Turn on **only** when the API sits behind a reverse proxy, and then list that proxy. Enabling it
without `KnownProxies` trusts `X-Forwarded-For` from anyone, so a client can claim a new source
address per request and rate limiting stops working while still appearing to. Startup refuses
that combination.

---

## 4. First login

The administrator is seeded on first start from `Auth:SeedAdmin`, and **never updated
afterwards** — so a password an operator rotated survives every later deployment.

```bash
curl -X POST http://localhost:5261/api/v1/auth/login \
     -H 'Content-Type: application/json' \
     -d '{"username":"admin","password":"…"}'
```

The response carries `mustChangePassword: true`. Rotate before doing anything else:

```bash
curl -X POST http://localhost:5261/api/v1/auth/password \
     -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
     -d '{"currentPassword":"…","newPassword":"…"}'
```

The configured value is a bootstrap credential, not a permanent one. Remove it from
configuration once rotated.

---

## 5. Upgrading

```bash
# 1. Apply the new version file to every database that needs it.
psql -U trinetra -d trinetra -f db/versions/v1.1.sql

# 2. Deploy the binaries.
```

Order is yours to manage; nothing enforces or verifies it.

The rule that makes a rolling deploy survivable: **a schema change must be backward-compatible
for the length of the deploy**, because the old binaries keep running against the new schema
until they are replaced. Additive changes during the window; anything destructive waits for a
later version, once the fleet is uniform.

---

## 6. What runs where

| Process | Owns |
|---|---|
| `Trinetra.Federation.Api` | HTTP surface, authentication, configuration writes, event queries, partition maintenance and retention |
| `Trinetra.Federation.Worker` | Claims connector targets by lease, polls vendor devices, normalises and persists events |
| `tools/Trinetra.Admin` | Schema preparation, provisioning from a terminal, connection testing |

Maintenance runs inside the API on a five-minute loop: partition creation ahead of need, and the
stale connection-test sweep. Both are idempotent, so every instance running them is harmless.

Retention is different — it deletes irreversibly, so it runs **once per day for the whole fleet**,
claimed through `federation.try_claim_daily_job`. Every instance attempts it; the database decides
which one wins. No leader election, the same reasoning as the connector leases.

---

## 7. When something is wrong

### The service refuses to start

| Message | Meaning |
|---|---|
| `Auth:Jwt:SigningKeys is not configured` | An unsigned or predictably-signed token is the same as no authentication |
| `Retention:AuthAuditMonths is N` | An authentication-audit retention below one month |
| `Refusing to start in Production using secrets that are committed to the repository` | A `CHANGE_ME` was left in `/etc/trinetra/api.env`, so the service fell back to the committed development values |
| `Retention:EventDays is N. The minimum is 7 days` | A retention period that would destroy data |
| `Network:TrustForwardedHeaders is enabled but no … KnownProxies` | Would silently disable rate limiting |

Every one of these is checked at startup rather than trusted, because each fails *invisibly* at
runtime: the service reports healthy and misbehaves only under the conditions nobody tests.

### Useful queries

```sql
-- What retention is actually holding, and how much disk it is using.
SELECT * FROM federation.retention_status ORDER BY table_name;

-- Rows landing in DEFAULT because their partition was missing. Alert on non-zero:
-- it degrades query plans silently and blocks the correct partition from being created.
SELECT * FROM federation.event_partition_health;

-- What the last retention pass removed.
SELECT job, last_run_date, detail FROM federation.maintenance_run;

-- Who changed what.
SELECT changed_at, actor, action, entity_type, entity_id
FROM federation.config_audit ORDER BY changed_at DESC LIMIT 50;

-- Who logged in, who failed, who locked out, and from where. Failures are coarse (a real
-- account with a wrong password and an unknown username are both 'login.failure'); a
-- presented_username with no user_id is the unknown-name case. Prefer GET /api/v1/auth-audit
-- (needs authaudit.read) — this is the raw table.
SELECT occurred_at, event_type, outcome, user_id, presented_username, source_address
FROM federation.auth_audit ORDER BY occurred_at DESC LIMIT 50;

-- Recent authentication trouble only.
SELECT occurred_at, event_type, source_address, presented_username
FROM federation.auth_audit WHERE outcome <> 'success'
ORDER BY occurred_at DESC LIMIT 50;

-- Who resolved which credential, for which target. Repeated failures against one
-- reference mean a rotated secret nobody updated, or someone probing.
SELECT accessed_at, accessed_by, target_id, credential_reference, succeeded, failure_reason
FROM federation.credential_access_log ORDER BY accessed_at DESC LIMIT 50;

-- Hierarchy reconciliation: ACTIVE org units / geographic areas left under an INACTIVE
-- ancestor. Attaching a live node under a retired one is rejected, and deactivation serializes
-- on an advisory lock, but a create landing in the instant an ancestor is cascaded can still
-- strand one. Not a scope leak — resolution ignores status — but lists and reports will show
-- it. Re-run the deactivation on each INACTIVE parent to clear.
SELECT c.id, c.code, c.name, 'organization_unit' AS kind
FROM federation.organization_units p
JOIN federation.org_unit_descendants(p.id) d ON d.id <> p.id
JOIN federation.organization_units c ON c.id = d.id AND c.status = 'ACTIVE'
WHERE p.status = 'INACTIVE'
UNION
SELECT c.id, c.code, c.name, 'geographic_area'
FROM federation.geographic_areas p
JOIN federation.geographic_area_descendants(p.id) d ON d.id <> p.id
JOIN federation.geographic_areas c ON c.id = d.id AND c.status = 'ACTIVE'
WHERE p.status = 'INACTIVE';
```

### A cross-organization unit move changed who can see a subtree

`POST /api/v1/organization-units/{id}/move` re-parents a unit under another organization and
rewrites the whole subtree's `organization_id`. Access-group `ORGANIZATION` scopes keep pointing
at the moved unit ids, so any group scoped near either attachment point silently gains or loses
that subtree. The move itself is one `config_audit` row carrying `subtreeUnitsMoved`,
`affectedGroups`, `camerasFollowing` and `targetsFollowing` — review it after the fact. The
access change applies as caches lapse: the API-key grant cache (~45s) and user access tokens
(~15 min, on next refresh), same as any other scope edit. To see which groups a pending move
would touch without applying it, send it without `confirmScopeImpact` and read the 409 body.

```sql
-- Access groups whose ORGANIZATION scope points into a unit subtree (blast radius of a move).
SELECT ag.id, ag.code, ag.status
FROM federation.access_groups ag
JOIN federation.group_scopes gs ON gs.group_id = ag.id
JOIN federation.scopes s ON s.id = gs.scope_id AND s.scope_type = 'ORGANIZATION'
WHERE s.organization_unit_id IN (SELECT id FROM federation.org_unit_descendants('<unit-id>'));
```

### The AI worker gets 403 or 404 resolving a stream credential

`GET /api/v1/vms/{id}/credential/resolve` needs the `credential.resolve` permission, which only
the `DETECTION_WORKER` role carries (added in `db/versions/v1.5.sql` — a database that stopped at
v1.4 will 403). A 404 means either the target is outside the worker key's access-group scope, or
the target has no credential stored yet (`GET /vms/{id}/credential/status` distinguishes them). A
503 means the resolution succeeded but its `credential_access_log` row could not be written and
the secret was withheld — check database health and retry.

To bring an existing worker key up to the current contract — its group on the
`DETECTION_WORKER` role, `ACTIVE`, holding all four grants (`vms.read`, `observation.write`,
`worker.heartbeat`, `credential.resolve`) — without re-issuing it:

Set `v_key_name` at the top of the script to the key's display name (it defaults to the name
`create-detection-api-key.sh` uses), then run the whole file — `psql "$DSN" -f
scripts/check-detection-api-key.sql`, or paste it into the VS Code SQL editor. It is a single
PL/pgSQL block: no bind parameters, so no client prompts; progress prints as `NOTICE`s. It
reports before/after, then grants the permission, re-roles and activates the group — all
idempotent. Organization/geography scope is not touched — set that with
`create-detection-api-key.sh` or `POST /api/v1/access-groups/{id}/scopes`.

### AI-worker health (`ai_worker_health`, `v1.13`)

A worker is identified by the API key it authenticates with **plus** its own `workerId`
(`ai-worker-{index}-of-{count}`) and `hostname`. A heartbeat can only ever create or refresh a
row under its own key — one integration cannot report liveness for another's workers, and a
user token cannot heartbeat at all (403).

`last_heartbeat_at` is the **server** clock, set on every heartbeat. The worker's own claimed
time is stored separately as `reported_at` and surfaced as `clockDriftSeconds`
(= `last_heartbeat_at − reported_at`) in the list. That figure always includes network and queue
latency, so a small positive value is normal; only a large magnitude means that host's clock is
wrong — never that the worker is unhealthy.

Staleness is judged **only** on `last_heartbeat_at`. Workers heartbeat every
`BACKEND_HEARTBEAT_INTERVAL_SECONDS` (default **15 s**, in `ai-worker`'s config); treat a row as
stale once `last_heartbeat_at` is older than roughly **3× that interval (~45 s)**. The API does
not return the interval — a monitoring consumer sets its own cutoff from the deployed value.

`GET /api/v1/worker-health` needs `worker.read` (SUPER_ADMIN, STATE_ADMIN, DEPARTMENT_ADMIN).
`DELETE /api/v1/worker-health/{id}` needs `worker.manage` (SUPER_ADMIN, STATE_ADMIN) and is
audited.

**After a fleet resize** (`WORKER_COUNT` 4 → 2): `ai-worker-2-of-4` and `ai-worker-3-of-4` stop
reporting and their rows go stale permanently — every consumer reads that as a crash. Clear
them with `DELETE /api/v1/worker-health/{id}` (get the ids from the list). There is no
automatic pruning yet.

### Permission coverage

The API logs its authorization surface at startup:

```text
Permission coverage: 49 of 52 route(s) declare a required permission.
```

A warning naming specific routes means an authorized endpoint declares no permission and is
reachable by any authenticated user unless its own handler checks one. The three routes that
legitimately declare none are anonymous: login, health, and the OpenAPI document.

---

## 8. Capacity notes

| Setting | Why it is explicit |
|---|---|
| `Maximum Pool Size=40` | `(api instances × pool) + (workers × pool) < postgres max_connections`. Exceeding it fails at the database as "too many clients", which looks like an outage rather than a capacity setting. Above roughly four API instances, put PgBouncer in transaction mode in front rather than raising this |
| `Timeout=10` | The wait for a pooled connection, deliberately shorter than `Command Timeout`. A saturated pool should surface quickly as backpressure, not as a slow request |
| `Application Name` | What makes `pg_stat_activity` readable when diagnosing the 80k simulation |

Two things do **not** scale with instance count, and both are quiet about it:

- **Rate limits are per process.** `instances × limit` is the fleet's real ceiling. The login
  limit is a credential-stuffing control and belongs at the reverse proxy — see `DEPLOYMENT.md`
  §1.
- **Server GC is set per project, not inherited.** The API gets it from the Web SDK; the worker
  sets it explicitly in its csproj because the Worker SDK defaults to workstation GC — one heap
  and one collection thread for a process holding up to `Worker:MaxTargets` connectors. A new
  host project needs the same line or it quietly runs single-heap.

Event queries are bounded by design: `from` and `to` are required, the window is capped, page size
is capped, and paging is keyset rather than OFFSET. These are not tuning knobs — they are what
stops one request from scanning a billion rows.
