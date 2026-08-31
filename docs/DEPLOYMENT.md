# Deployment — on-prem bare metal

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
