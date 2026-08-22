# Operations — Model 3

How to prepare a database, configure a deployment, start the services, and upgrade them.

`ARCHITECTURE-MODEL-3.md` explains *why* the system is shaped this way. This document is what
you follow to run it. `DEPLOYMENT.md` covers installing it across real hosts — topology, systemd
units, and rolling upgrades.

---

## 1. The order that matters

```text
   docker compose up -d          database is running
            |
            v
   psql -f db/versions/v1.sql    create the schema
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

### Versions

```text
   db/versions/v1.sql       everything for v1 — tables, functions, views, reference data
   db/versions/v1.1.sql     only what v1.1 adds, applied on top of a v1 database
```

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

```bash
docker compose up -d      # skip if you have your own PostgreSQL
psql -U trinetra -d trinetra -f db/versions/v1.sql
```

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
| `Auth:Jwt:SigningKey` | `openssl rand -base64 48` | Startup refuses rather than run with a guessable key. A leaked key lets anyone forge a session as any user |
| `Auth:SeedAdmin:Password` | A strong value you will rotate at first login | Startup refuses an empty or short value rather than create a weak administrator |
| `Auth:AllowedOrigins` | Where the frontend is served from | Never a wildcard. Requests carry credentials, so a wildcard lets any site an operator visits act as them |

> **`Secrets:Key` must never change once credentials are stored.** Every stored camera password
> becomes permanently unreadable and must be re-entered by hand across the whole estate.
> `Secrets:KeyId` records which key sealed each secret, so rotation can be staged rather than
> done as a flag day.

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
  "AuditMonths": 84,
  "ConnectionTestDays": 30,
  "DeadLetterDays": 90
}
```

**These are a policy decision, not a technical one.** CCTV event metadata is subject to statutory
retention rules that differ by state, by department and by the purpose the cameras serve. The
defaults are sized for disk, not for law. Confirm them against the retention policy the
deployment is bound by before going live.

Startup refuses any period below **7 days**. The realistic mistake is a misplaced digit, and
retention drops partitions — immediate, irreversible, no recycle bin.

`Enabled: false` exists so deletion can be paused during an investigation. It is not a safe
long-term setting: `federation_event` takes 100–400M rows/day, so leaving it off is a disk-full
outage on a schedule.

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
| `Auth:Jwt:SigningKey is not configured` | An unsigned or predictably-signed token is the same as no authentication |
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
```

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

Event queries are bounded by design: `from` and `to` are required, the window is capped, page size
is capped, and paging is keyset rather than OFFSET. These are not tuning knobs — they are what
stops one request from scanning a billion rows.
