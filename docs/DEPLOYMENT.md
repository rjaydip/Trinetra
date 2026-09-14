# Deployment

The primary target is on-prem bare metal with systemd (§1–§7). §8 covers the equivalent
container image for environments that prefer it — same binary and configuration, pick one per
environment.

There is no Kubernetes, no service mesh and no orchestrator. Scaling out is starting more worker
processes; the fleet rebalances itself through the PostgreSQL lease. This document covers laying
that out on real hosts.

The database provider is deployment-specific and is deliberately not fixed in this document.
The current development environment uses Supabase-hosted PostgreSQL; production may use another
PostgreSQL provider or an on-premises PostgreSQL installation. Configure the selected provider in
`ConnectionStrings:Federation` or through `ConnectionStrings__Federation`.

`OPERATIONS.md` covers configuration and day-to-day running. This is about installing it.

---

## 1. Topology

```text
                    ┌──────────────────────────┐
   operators ──────>│  reverse proxy (TLS)     │
   frontend         └────────────┬─────────────┘
                                 │
                    ┌────────────┴─────────────┐
                    │  API hosts  (1..4)       │   trinetra-api.service
                    │  stateless, any can      │
                    │  serve any request       │
                    └────────────┬─────────────┘
                                 │
                    ┌────────────┴─────────────┐
                    │  PostgreSQL 17 (stock)   │   the only shared state
                    │  primary + replica       │
                    └────────────┬─────────────┘
                                 │
                    ┌────────────┴─────────────┐
                    │  worker hosts  (1..N)    │   trinetra-worker@1..n
                    │  each claims a bounded   │
                    │  share of VMS targets    │
                    └────────────┬─────────────┘
                                 │
                          camera / NVR networks
```

Two properties follow from this and are worth stating plainly.

**API hosts are interchangeable and hold nothing.** Losing one loses in-flight requests and
nothing else. Above roughly four instances, put PgBouncer in transaction mode in front of
PostgreSQL rather than raising `Maximum Pool Size` — see `OPERATIONS.md` §8.

> **Rate limiting does not survive scale-out on its own.** The limiter is in-process, so each API
> host keeps its own counters and the fleet's effective limit is `instances × the configured
> value`. For the per-user global limit that is merely generous. For **login** it is a security
> control being diluted: 10 attempts/minute/IP becomes 40 across four hosts, silently, at the
> moment you scale out to handle more load. Enforce the login limit at the reverse proxy, which
> is the only place that sees the whole fleet's traffic, and treat the in-process limit as a
> backstop for a single host rather than as the fleet's control.

**Worker hosts are not interchangeable in the moment, but recover without intervention.** A
worker holds leases on specific targets. When it dies those leases expire on their TTL and other
workers claim them; when it stops cleanly it releases them immediately, which is the difference
between "targets dark for a full TTL" and "dark for the restart".

### Sizing workers

```text
   fleet capacity = instances × Worker:MaxTargets
```

The unit of scale is **VMS instances, not cameras** — one `GetCamerasAsync()` returns a target's
whole inventory. A few hundred to a few thousand targets covers 80,000 cameras.

`MaxTargets` defaults to 100 and exists to cap blast radius: without a bound, whichever worker
starts first claims the entire estate and its failure becomes a total outage rather than a
partial one. Provision meaningful headroom — a fleet sized exactly to its target count cannot
absorb the loss of a host.

---

## 2. Prerequisites per host

| | API host | Worker host | Database host |
|---|---|---|---|
| .NET 10 runtime | yes | yes | — |
| Reaches PostgreSQL | yes | yes | — |
| Reaches camera/NVR networks | no | **yes** | no |
| Reachable from operators | via proxy | no | no |

Worker hosts are the ones that touch the least trustworthy network in the system. They should be
segmented toward their VMS subnets only, and should not be reachable from the operator network at
all.

**PostGIS, database host, one-time.** `db/versions/v1.19.sql` runs `CREATE EXTENSION IF NOT
EXISTS postgis;`. The extension package must be installed on the Postgres host first (e.g.
`postgresql-17-postgis-3` for stock PostgreSQL 17 — match whatever package name your distro uses
for the Postgres major version actually running), and `CREATE EXTENSION` itself needs superuser
or a role the DBA has pre-granted `CREATE` on the database to. This is a manual operator step on
the database host before `v1.19.sql` is applied — the API process never migrates (see the
Dockerfile's own header comment) and does not install extensions at startup.

---

## 3. Install

Once, from any host that can reach the database:

```bash
psql -U trinetra -d trinetra -f db/versions/v1.sql
```

Then on each host:

```bash
# 1. Publish. To a staging path, then swap -- a failed build must never leave a half-copied
#    tree behind a running unit.
dotnet publish src/Trinetra.Federation.Api -c Release -o /opt/trinetra/api.staged
mv /opt/trinetra/api /opt/trinetra/api.previous 2>/dev/null || true
mv /opt/trinetra/api.staged /opt/trinetra/api

# 2. Service account and ownership. No shell, no home: it exists to own processes and files.
useradd --system --no-create-home --shell /usr/sbin/nologin trinetra 2>/dev/null || true
chown -R root:trinetra /opt/trinetra/api && chmod -R o-rwx /opt/trinetra/api

# 3. Secrets. 0640, root-owned, never world-readable.
install -d -o root -g trinetra -m 0750 /etc/trinetra
install -o root -g trinetra -m 0640 deploy/systemd/api.env.example /etc/trinetra/api.env

# 4. Units.
install -m 0644 deploy/systemd/trinetra-api.service /etc/systemd/system/
install -m 0644 'deploy/systemd/trinetra-worker@.service' /etc/systemd/system/
systemctl daemon-reload
```

Keeping `api.previous` is what makes a rollback a `mv` rather than a rebuild. Do not overwrite an
existing `/etc/trinetra/*.env` on an upgrade — that is where the real secrets live.

### Fill in the secrets

```bash
sudo $EDITOR /etc/trinetra/api.env       # root:trinetra 0640
sudo $EDITOR /etc/trinetra/worker.env
```

Every `CHANGE_ME` must be replaced. **The API refuses to start in Production if any secret still
matches the value committed in `config/trinetra.settings.json`.**

That check exists because the committed settings file is copied into the publish output next to
the binaries. Environment variables override it — which is how a deployment supplies real values
— but nothing forces them to be set. Miss one line and the service starts, reports healthy, and
runs on a key that anyone with read access to the repository already has. There is no runtime
symptom: every credential encrypts and decrypts correctly, every token validates.

> `Secrets:Key` can **never** be changed once credentials are stored. Set it before provisioning
> anything. `Secrets:KeyId` records which key sealed each secret, so rotation can be staged.

The API and every worker must share the same `Secrets__Key`. A worker with a different one cannot
decrypt what the API stored and reports it as corruption rather than as misconfiguration.

### Start

```bash
sudo systemctl enable --now trinetra-api
sudo systemctl enable --now trinetra-worker@1 trinetra-worker@2 trinetra-worker@3
```

---

## 4. The units

| Unit | Type | Notes |
|---|---|---|
| `trinetra-api.service` | notify | One per host |
| `trinetra-worker@.service` | notify | **Template.** One instance per worker process |

Both are `Type=notify` and the hosts call `AddSystemd()`, so systemd learns the service is ready
only when startup has genuinely completed — for the API, after the admin seed. Without that,
"started" means "the process exists", and a rolling restart can move faster than the fleet can
actually come up.

`WorkerId` is set by the template unit to `%H-%i` — hostname and instance number. This is not
cosmetic: `connector_target.leased_by` records it, so when a target is not being polled you can
tell which unit on which host is meant to own it. The code default is `MachineName-ProcessId`,
which changes on every restart and makes that correlation impossible.

Both units are hardened with `ProtectSystem=strict` and no writable paths at all — logs go to the
journal, state lives in PostgreSQL. Adding file logging later means adding a matching
`LogsDirectory=` or the process fails on first write.

`MemoryDenyWriteExecute=false` is stated explicitly in both. The .NET JIT maps pages writable and
then executable, so W^X cannot be enforced; it is written down so nobody "hardens" it and spends
an afternoon on the resulting crash.

The worker template deliberately omits `PartOf=`. Workers are independent — restarting one must
not disturb the others, and a lease held by a healthy worker should not be released because a
sibling failed.

---

## 5. Upgrades

**Schema first, then binaries.** Nothing enforces that order — enforce it yourself.

```bash
# 1. Apply the schema change to every database that needs it.
psql -U trinetra -d trinetra -f db/versions/0012_whatever.sql

# 2. Regenerate the tracked artefacts and commit them.


# 3. Then each host, in any order. Rolling is fine.
#    republish as above, then
sudo systemctl restart trinetra-api
```

No running service touches the schema, and none verifies it. A binary deployed ahead of its
schema starts, reports healthy, and fails requests with `relation ... does not exist` — which
reads as a code fault rather than a deployment ordering mistake. There is no guard; the order is
operational discipline.

The rule that makes rolling deploys survivable: **a schema change must be backward-compatible for
the length of the deploy**, because the old binaries keep running against the new schema until
they are replaced. Additive changes during the window; anything destructive is a separate, later
change once the fleet is uniform.

**Editing the seeded preset roles.** Since `v1.11` the seeded roles are editable presets. If a
future migration re-seeds a preset's name, description or permission set, it **must** scope those
writes to `WHERE customized_at IS NULL` — a non-NULL `customized_at` means an operator
deliberately changed that preset and the change must not be silently reverted on upgrade. A
genuinely new permission a preset should carry is added with an explicit unconditional statement
(like the `SUPER_ADMIN` backfill), with a comment saying why it overrides customization. The
`SUPER_ADMIN` permission backfill is always unconditional — that role is never customizable.


### Rolling a worker fleet

```bash
for i in 1 2 3 4; do
    sudo systemctl restart "trinetra-worker@$i"
    sleep 20        # let it claim before disturbing the next
done
```

`TimeoutStopSec=45` gives a worker time to release its leases rather than let them expire. Restart
one at a time: restarting the fleet at once means every target is unclaimed simultaneously, and
they all come back competing for the same leases.

### Rollback

```bash
sudo systemctl stop trinetra-api
sudo mv /opt/trinetra/api /opt/trinetra/api.failed
sudo mv /opt/trinetra/api.previous /opt/trinetra/api
sudo systemctl start trinetra-api
```

Binaries roll back cleanly. **Schema does not** — a `db/versions/` file is forward-only unless you
write the reverse yourself. Test a schema change against the previous build before applying it to
production, because that is the combination a rollback produces.

---

## 6. Verifying a deployment

```bash
systemctl status trinetra-api 'trinetra-worker@*'
journalctl -u trinetra-api -n 50 --no-pager
```

The API logs its authorization surface at startup, which is a useful smoke signal:

```text
Permission coverage: 49 of 52 route(s) declare a required permission.
```

Then check the fleet is actually working, not merely running:

```sql
-- Every target should be leased, and by a worker id you recognise.
SELECT code, state, leased_by, lease_expires_at
FROM federation.connector_target ORDER BY leased_by NULLS FIRST;

-- Targets nobody holds. Non-empty for more than a lease TTL means the fleet is under-provisioned
-- or a vendor filter excludes them.
SELECT count(*) FROM federation.connector_target
WHERE state = 'Active' AND (leased_by IS NULL OR lease_expires_at < now());

-- Recent health. cursor_lag_seconds climbing is the signal that matters: a connector can return
-- 200 OK while falling hours behind.
SELECT target_id, status, cursor_lag_seconds, checked_at
FROM federation.connector_health
WHERE checked_at > now() - interval '10 minutes'
ORDER BY cursor_lag_seconds DESC NULLS LAST LIMIT 20;
```

A unit that starts and immediately fails is almost always a `CHANGE_ME` left in place or an
unreachable database; both name themselves in the journal. A unit that starts *successfully* and
then fails every request is almost always a schema that was never applied — nothing checks it, so
it will not tell you.

---

## 7. What is not covered here

Stated rather than implied, because each is real work that a production install needs.

- **TLS termination.** The API speaks plain HTTP and expects a reverse proxy in front. If that
  proxy is present, `Network:TrustForwardedHeaders` must be enabled **and** `KnownProxies` set —
  enabling it without them lets any client forge a source address, which silently disables rate
  limiting. Startup refuses that combination.
- **Database HA and backups.** Model 3 assumes PostgreSQL is somebody's job: streaming replication,
  PITR, and a tested restore. Retention drops partitions irreversibly, so backup coverage must
  extend at least as far back as the retention window.
- **Secret management.** `credential_reference` is designed to point at Vault or equivalent; the
  current implementation seals credentials with a local key. Moving to a KMS-wrapped DEK is the
  intended path and is not yet built.
- **mTLS between platform services.** `ARCHITECTURE-MODEL-3.md` §9 calls for it. Not implemented.
  This matters most for `GET /api/v1/vms/{id}/credential/resolve` — the one route that returns a
  plaintext credential (used by Model 2's AI worker to reach camera RTSP streams). Until mTLS is
  in place, keep that traffic on a segmented management network and terminate TLS at the proxy;
  do not expose the API's plain-HTTP port beyond it. The worker's `DETECTION_WORKER` API key
  must be provisioned with an access-group scope that covers exactly the targets it processes.
- **Log shipping.** Everything goes to journald. Forwarding to a central collector is deployment
  policy, not application configuration.
- **Kafka.** Not involved. Events are read from PostgreSQL.

---

## 8. Container image (alternative to systemd)

The root `Dockerfile` builds the API into a container that is an equivalent target to the
bare-metal unit above — same binary, same configuration model. Pick one per environment; the
database, schema discipline (§5) and everything in §7 are unchanged.

```bash
docker build -t trinetra-api .
cp deploy/docker/api.env.example api.env      # then edit
docker run -d --name trinetra-api -p 5261:8080 --env-file api.env --restart on-failure trinetra-api
```

- **Config — every setting is a parameter.** `deploy/docker/api.env.example` is the full
  reference: the connection string, the whole `Auth` / `Auth:Jwt` block, the seed-admin account,
  `Auth:AllowedOrigins` (CORS), the `Network` proxy settings, every `Retention` period, and the
  listen port. Any key in `config/trinetra.settings.json` maps to an env var by replacing `:`
  with `__`; an array element takes a trailing `__0`, `__1`. The image bakes only
  `config/trinetra.settings.example.json` (every secret `CHANGE_ME`); real values come from
  `--env-file` / `-e`. Four are hard-required — `ConnectionStrings__Federation`, `Secrets__Key`,
  `Auth__Jwt__SigningKey`, `Auth__SeedAdmin__Password` — the rest have working defaults. Never
  bake a secret into an image or put it on the `docker build` command line.
- **Port.** Kestrel listens on `ASPNETCORE_HTTP_PORTS` (default `8080`) inside the container;
  `-p <host>:8080` publishes it. TLS still terminates at a proxy in front (§7).
- **The container never migrates the database.** Point `ConnectionStrings__Federation` at a
  database that already carries the schema — `db/full-schema.sql` for a fresh one (plain SQL,
  runs via psql, a GUI client, or one `NpgsqlCommand`), or `db/versions/*.sql` in order for an
  upgrade (§5). On start the API only verifies it can reach that schema (fails fast if a table
  is missing) and inserts the one bootstrap admin row if the account is absent. Nothing in the
  running container touches DDL.
- **Docs / first login.** `GET /` redirects to `/scalar` — the interactive API reference, served
  in every environment (every route is still authenticated; use a bearer token from
  `POST /api/v1/auth/login`, or the OAuth2 password flow in Scalar's Authorize dialog).
- **Health.** `HEALTHCHECK` hits the anonymous `/health`; `docker inspect -f '{{.State.Health.Status}}'`
  reports it. `start-period` covers the schema check + admin seed on a cold database.
- **Confirming the config.** Open `/scalar` — the "This deployment" table at the top of the page
  shows the database name / host / port it connected to, the PostgreSQL version, the CORS
  origins, and the retention windows. Enough to tell two environments apart; no key, credential
  or account information.
- **Runs unprivileged** as the image's `app` user — keep it that way, this process holds the
  credential encryption key.
- **Data Protection keys.** ASP.NET writes them under `/home/app/.aspnet/DataProtection-Keys`,
  which is ephemeral. Auth is JWT (stateless) so a restart is harmless today, but for more than
  one instance mount a shared volume there or configure a persistent key ring.

## 9. Streaming gateway (video wall live view)

Optional — only needed once the video wall (`frontend/src/features/videowall/`) is meant to show
live video rather than status-only tiles. Full design: `docs/STREAMING-GATEWAY-PLAN.md`. Two new
processes, both bare-metal/systemd like everything else in this document, no container path yet:

- **`trinetra-mediamtx`** — [MediaMTX](https://github.com/bluenviron/mediamtx), a third-party
  binary (not built from this repo), pulls each camera's RTSP feed on-demand and remuxes it to
  HLS. Config: `deploy/mediamtx/mediamtx.yml`. Unit: `deploy/systemd/trinetra-mediamtx.service`.
  Both its HLS and its runtime config API listen on `127.0.0.1` only — never expose this host's
  MediaMTX ports directly; `Federation.Api` is the only thing that talks to it (below).
- **`trinetra-mediamtx-provisioner`** — `deploy/mediamtx/provision_paths.py`, a small standalone
  Python script (its own `requirements.txt`, no dependency on `ai-worker/`). Polls the camera
  registry, resolves each camera's credential through `GET
  /api/v1/cameras/{id}/credential/resolve` (new — see G1 in the plan; requires an API key for an
  access group holding **only** the `STREAMING_GATEWAY` machine role, never `DETECTION_WORKER`'s
  key), and keeps MediaMTX's runtime path list in sync. Env: copy
  `deploy/mediamtx/mediamtx-provisioner.env.example` to `/etc/trinetra/mediamtx-provisioner.env`
  (`chmod 600` — it holds an API key) and fill in `TRINETRA_API_KEY`. Unit:
  `deploy/systemd/trinetra-mediamtx-provisioner.service` (`BindsTo=trinetra-mediamtx.service` —
  the two start and stop together).

```bash
# One-time: install MediaMTX itself (pin a version; do not track `latest` in a runbook).
curl -L -o /tmp/mediamtx.tar.gz \
  https://github.com/bluenviron/mediamtx/releases/download/vX.Y.Z/mediamtx_vX.Y.Z_linux_amd64.tar.gz
mkdir -p /opt/trinetra/mediamtx
tar -xzf /tmp/mediamtx.tar.gz -C /opt/trinetra/mediamtx mediamtx
cp deploy/mediamtx/mediamtx.yml /opt/trinetra/mediamtx/mediamtx.yml

# Provisioner's own venv (standalone; keep it out of ai-worker's).
python3 -m venv /opt/trinetra/mediamtx/.venv
/opt/trinetra/mediamtx/.venv/bin/pip install -r deploy/mediamtx/requirements.txt
cp deploy/mediamtx/provision_paths.py /opt/trinetra/mediamtx/provision_paths.py

cp deploy/mediamtx/mediamtx-provisioner.env.example /etc/trinetra/mediamtx-provisioner.env
chmod 600 /etc/trinetra/mediamtx-provisioner.env   # then edit — TRINETRA_API_KEY

useradd --system --no-create-home --shell /usr/sbin/nologin trinetra-mediamtx
chown -R trinetra-mediamtx:trinetra-mediamtx /opt/trinetra/mediamtx

cp deploy/systemd/trinetra-mediamtx.service deploy/systemd/trinetra-mediamtx-provisioner.service \
  /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now trinetra-mediamtx trinetra-mediamtx-provisioner
```

**The public-facing piece: `Federation.Api` itself — no third process.** `GET
/api/v1/streams/{cameraId}/session` (`camera.read`-gated like any other camera-scoped read)
mints a 5-minute JWT scoped to one camera. The browser holds it and sends `Authorization: Bearer
<token>` on every HLS request it makes to `GET /api/v1/streams/{cameraId}/{*hlsPath}` — same
host and port as the rest of the API, nothing extra to deploy. That route
(`StreamSessionEndpoints.ProxyAsync`) validates the token in-process, cross-checks its
authorized camera id against the one in the URL, and reverse-proxies the request to MediaMTX's
`127.0.0.1:8888` via a named `HttpClient` (`StreamingOptions:MediaMtxBaseUrl`, defaults to that
address — override it if MediaMTX runs on a different host/port than the API). TLS termination
(§7) is unaffected — it's the same API process behind whatever already terminates TLS today.

A standalone `GET /api/v1/streams/validate` (`AllowAnonymous`) still exists for a deployment
that would rather front MediaMTX with its own reverse proxy instead (e.g. `deploy/nginx/streaming-gateway.conf`,
kept in the repo as that alternative) — not required for the default path above.

With G1-G5 in place, `GET /streams/{cameraId}/session` → `Authorization: Bearer` on every
`GET https://<host>/api/v1/streams/{cameraId}/index.m3u8` (and its segment requests) is the whole
path from an authorized browser to that camera's live view, served entirely by the API and
MediaMTX — two processes, not three.
