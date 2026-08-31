# Model 3 — Production Architecture

Implementation architecture for the VMS Federation & Middleware layer described in
`MODEL-3-VMS-FEDERATION-MIDDLEWARE.md`. This document records the decisions that the
code depends on; change the code and this document together.

**Target:** 80,000 cameras in production, 100+ cameras in first-phase validation.
**Runtime:** .NET 10 / ASP.NET Core minimal API, on-prem bare metal, horizontally scalable.

---

## 1. The scale insight

Model 3 does not decode, transcode, or move video. That is Model 2's job. Model 3
federates **metadata and events**. This single fact determines the sizing of everything
below, so it is worth stating plainly:

> The unit of scale is not 80,000 cameras. It is the few hundred to few thousand
> **VMS instances** those cameras live behind, and the aggregate event rate they emit.

An 80,000-camera estate is typically 500–2,000 connector targets (an NVR handles 32–128
cameras; an enterprise Milestone/Genetec site handles thousands). That is the real
parallelism unit, and it is a comfortable number.

### The rule this produces

**Never poll per camera. Poll per VMS.**

| Approach | Requests/sec against vendor devices @ 80k cameras, 30s interval |
|---|---|
| Per-camera polling | ~2,670 — vendor NVRs collapse well before this |
| Per-VMS bulk inventory | ~17–67 — trivial |

One `get_cameras()` call returns that VMS's entire inventory. Any code path that issues
work proportional to camera count against a vendor device is a defect, not a tuning
problem. This is enforced in the adapter contract: inventory and status methods are
VMS-scoped and return collections, never single-camera lookups.

### How a camera reaches the platform is configuration

Each `ConnectorTarget` names its own `Vendor`, so the operator chooses per site how that site is
federated. Most cameras are reached through their NVR or VMS — one target covering many cameras.
ONVIF is one option among several, used where it is configured, typically for a recorder-level
endpoint or for a camera with no VMS behind it.

Worth knowing when planning an estate: a target pointed at a single ONVIF camera covers one
camera, so a site federated that way needs one target per camera rather than one per recorder.
That is a sizing consideration for the deployment, not a rule the code enforces. `ConnectorHealth`
reports cameras-per-target so the ratio stays visible.

---

## 2. Sizing envelope

Event volume was left open ("design for growth"), so the system is built for the
**medium band with explicit headroom**, and the components that would have to change on
crossing into the high band are named.

| Band | Sustained rate | Events/day | What it is |
|---|---|---|---|
| Low | 100–500/s | 10–40M | Connectivity, up/down, maintenance, recording state |
| **Medium (design point)** | **1–5k/s** | **100–400M** | Above + VMS-side motion, line-crossing, tampering, analytics |
| High | 10–30k/s | 1–2.5B | Above + every Model 2 ANPR read and person/vehicle detection |

**Built for medium, degrades gracefully, does not silently fall over at high.** Peak
bursts are absorbed by Kafka rather than by the database.

### What changes if you cross into the high band

Three things, and only three — they are isolated behind ports so the rest of the system
does not move:

1. **Correlation** moves from windowed SQL (`SqlCorrelationEngine`) to a stream
   processor (Flink, or Kafka Streams via a JVM service). The `ICorrelationEngine` port
   exists precisely so this swap does not touch callers.
2. **Event envelope** moves from `System.Text.Json` to Protobuf/Avro on the wire. The
   schema registry and versioned envelope exist for this; the domain model does not
   change, only the `IEventSerializer` implementation.
3. **Hot storage** moves from PostgreSQL daily partitions to a columnar store
   (ClickHouse) for the analytical path, with PostgreSQL retained for operational state.

Nothing else in this document is invalidated by that transition. That is the point of
designing for growth rather than designing for 30k/s on day one.

---

## 3. Component topology

```text
        Departmental sites                       Central platform
 ┌──────────────────────────┐           ┌────────────────────────────────┐
 │ Milestone / Genetec      │           │                                │
 │ Hikvision / Dahua NVRs   │◄──pull────┤   Connector Worker Pool        │
 │ ONVIF devices            │──push────►│   (asyncio, N processes)       │
 └──────────────────────────┘           │      │                         │
                                        │      │ normalise at the edge   │
   ┌──────────────────────┐             │      ▼                         │
   │ Native/.NET SDK      │◄──gRPC──────┤   Event Envelope (versioned)   │
   │ Sidecar (isolated)   │             │      │                         │
   └──────────────────────┘             └──────┼─────────────────────────┘
                                               ▼
                                     ┌───────────────────┐
                                     │  Kafka            │
                                     │  key = camera_id  │
                                     └─────────┬─────────┘
                        ┌──────────────┬───────┴──────┬──────────────┐
                        ▼              ▼              ▼              ▼
                 ┌───────────┐  ┌───────────┐  ┌───────────┐  ┌───────────┐
                 │Correlation│  │  Search   │  │  Alert    │  │ Event     │
                 │  Engine   │  │  Indexer  │  │  Engine   │  │ Persister │
                 └─────┬─────┘  └─────┬─────┘  └─────┬─────┘  └─────┬─────┘
                       │              ▼              │              ▼
                       │        OpenSearch           │        PostgreSQL
                       │      (rollover + ILM)       │      (time-partitioned)
                       └──────────────┬──────────────┘
                                      ▼
                              FastAPI  /api/*
                        (RBAC, department-scoped, audited)
```

### Runtime classes

Connector workers are grouped by **runtime class**, not by vendor. This is the isolation
boundary:

| Class | Transport | Packing | Blast radius of a crash |
|---|---|---|---|
| `managed` | Pure HTTP (ONVIF SOAP, ISAPI, Dahua CGI, Milestone Gateway REST, Genetec Web SDK) **and** the .NET-native Milestone MIP / Genetec SDKs | 50–200 targets per worker process | That worker's targets only; leases expire and are reclaimed |
| `native` | P/Invoke to a vendor C SDK (Hikvision `HCNetSDK`, Dahua `NetSDK`) | Segregated worker process, one vendor per process | That process only — a native access violation kills the CLR, so it must not share a process with managed targets |

Choosing .NET collapses the hardest part of this table. Milestone MIP SDK and Genetec
Security Center SDK are **.NET-only libraries**: on any other runtime they would need a
gRPC sidecar in a second language, and the two richest event sources in the estate would
sit behind a process hop. Here they are ordinary in-process references.

**But one consequence must be stated explicitly:** the Milestone MIP SDK and the Genetec
Security Center SDK are **Windows-only in practice**. Using them as in-process references would
imply a *Windows* worker fleet alongside the Linux one — an infrastructure fork, not a library
choice. Phase 1 therefore reaches those systems over HTTP (Milestone API Gateway REST/WebSocket,
Genetec Web SDK), which keeps the entire worker fleet homogeneous and Linux-only. Adopting either
.NET SDK is a deliberate decision to run a second fleet, and must be taken as such.

Two rules survive regardless:

- **Prefer the vendor's HTTP surface even when a SDK exists.** ONVIF, ISAPI, Dahua CGI,
  Milestone API Gateway and Genetec Web SDK are all reachable with `HttpClient`, are
  version-tolerant, and cannot fault the process. Reach for a native SDK only for
  capabilities the HTTP surface genuinely does not expose.
- **Native SDK targets never share a process with managed ones.** P/Invoke into a vendor
  C library can take down the runtime in a way no `try/catch` will save; process
  segregation is the only real containment. `RuntimeClass` on a connector target carries this,
  and `WorkerOptions.Validate()` refuses a Native worker that serves more than one vendor.

## 4. Work assignment without Kubernetes

Deployment is bare metal with no container orchestrator, but must scale horizontally.
So the platform provides its own assignment and failover, using PostgreSQL — which is
already a hard dependency — rather than adding ZooKeeper, etcd, or Consul.

**Lease-based claim.** Each connector target is a row. Workers claim batches with
`SELECT ... FOR UPDATE SKIP LOCKED`, hold a lease with a heartbeat, and renew it while
healthy. Workers are `BackgroundService` hosts supervised by systemd (`Type=notify`, so
systemd sees real readiness rather than just a live PID).

```text
worker starts
   │
   ├─► claim up to max_targets rows WHERE lease_expires_at < now()
   │      ORDER BY last_claimed_at ASC
   │      FOR UPDATE SKIP LOCKED
   │
   ├─► heartbeat every lease_ttl/3, extending lease_expires_at
   │
   └─► on clean shutdown: release leases immediately
       on crash:          leases expire after lease_ttl, another worker claims
```

Properties this buys:

- **Rebalancing is emergent.** Add a worker and it claims unleased targets. Kill one and
  its targets are reclaimed within `lease_ttl`. No coordinator, no split-brain election.
- **No orchestrator dependency.** systemd supervises processes; PostgreSQL arbitrates
  ownership. Both are things an on-prem ops team already runs.
- **Bounded per-worker load.** `max_targets` caps a worker so one process cannot claim
  the whole estate and then die with it.

Trade-off accepted: failover latency equals `lease_ttl` (default 45s), during which that
VMS's events are not being pulled. Push-subscribed events are buffered by the VMS where
the vendor supports it, and gap-filled from the cursor on reclaim (§6).

---

## 5. Event contract and delivery semantics

### Partitioning

Kafka partition key is **`camera_id`**. This gives per-camera ordering, which is exactly
the guarantee correlation needs (a camera's observations arrive in order) without paying
for a global order nobody needs. Cross-camera ordering is resolved by `timestamp`, not by
partition.

### Deduplication

Natural key is `(source_vms, source_event_id)`. VMS reconnects, subscription replays, and
lease handovers all resend events; every sink upserts on this key.

### Delivery

**At-least-once transport + idempotent sinks = effectively-once.** The system does not
claim exactly-once and no component may be written assuming it. Any consumer that is not
idempotent is a defect.

### Envelope

The event contract in `TECHNICAL-DESIGN.md` §6 is the payload. It is wrapped in a
versioned envelope so schema evolution never requires a coordinated stop:

```text
envelope: schema_version, event_id, produced_at, producer, trace_id
payload:  the common event schema (source_vms, camera_id, department_id,
          event_type, timestamp, severity, location, object_reference,
          confidence, source_event_id, raw_reference)
```

Consumers must accept unknown fields and reject unknown **major** versions. `raw_reference`
points at the retained vendor-native payload in object storage — never inlined, so a
normalisation bug is recoverable by replay rather than by re-pulling from the VMS.

---

## 6. Reliability mechanics

Each of these is per-connector-target, configurable per vendor, because vendor tolerance
varies by an order of magnitude.

| Mechanism | Purpose | Why per-target |
|---|---|---|
| **Token-bucket rate limit** | Never overwhelm a vendor device | A Milestone cluster tolerates 100x what a small NVR does |
| **Circuit breaker** | Stop hammering a failing VMS; fail fast | One dead site must not consume the worker's whole time budget |
| **Cursor / checkpoint** | Resume without loss or replay storm | Persisted `last_event_ts` + `last_event_id`, bounded lookback |
| **Disk spool** | Survive Kafka unavailability | Bare metal: no cloud queue to fall back on. Bounded size, then drop-with-counter |
| **Capability cache** | Never call a VMS to ask what it can do | Probed on connect, persisted, re-probed on an interval |

### Gap filling

On reclaim or reconnect, a pull adapter resumes from its persisted cursor with a bounded
lookback window. The bound matters: without it, a target that was down for a day returns
and floods the bus with a day of backlog at full rate. Backfill is rate-limited
separately from live tailing and is explicitly marked in the envelope so consumers can
distinguish a replay from live traffic.

**Exception — ONVIF cannot backfill at all.** ONVIF has no historical event query: a PullPoint
subscription returns only what has queued since it was created. So `Capability.EventsPull` is
genuinely *unsupported* on ONVIF targets, and a lease handover loses up to one `lease_ttl` of
events on those targets. This is unavoidable, not a defect to be engineered away. It must
therefore be **measured and operator-visible** — a counter on `ConnectorHealth`, never silence.
Targets that matter most for investigation should be federated through a VMS that supports
time-ranged event queries (Milestone does; ONVIF does not) rather than through ONVIF directly.

---

## 7. Storage tiering

Designed now, even where only the first tier is implemented, because retrofitting
retention onto an unpartitioned 400M-row/day table is not feasible.

| Tier | Store | Window | Serves |
|---|---|---|---|
| Hot | PostgreSQL, **daily-partitioned** | 30 days (configurable) | Operational queries, correlation window, API |
| Search | OpenSearch, rollover + ILM | 90–365 days | Free-text and faceted event search |
| Cold | Object store, Parquet | Retention policy | Audit, forensics, replay |

PostGIS lives in the same PostgreSQL instance and is shared with Model 1 — camera
location is Model 1's authoritative data, and Model 3 reads it rather than copying it.

**Partitioning is not optional.** At the medium design point this table grows by
100–400M rows/day. Partitions are created ahead of time by a maintenance job and dropped
wholesale at retention expiry; there is no `DELETE FROM events`.

### Camera status history is transitions, not samples

`camera_status_history` records a row only when a camera's `health`, `is_enabled` or
`is_recording` actually changes, carrying both the previous and the new value.

The alternative — a row per camera per status poll — is arithmetic rather than opinion:

```text
   80,000 cameras / 30s poll  =  2,667 rows/s  ≈  230M rows/day
```

That is the same order as `federation_event`, for data that changes a few thousand times a day.
Transitions answer every question samples would: the status at any past instant is the newest
row at or before it, and a timeline draws directly from the from/to pair on each row.

What transitions deliberately cannot answer is *"was this camera being polled at 10:31?"* — that
belongs to `connector_health`, which records the target's poll cadence, and to
`federated_camera.last_seen`.

Detection is a **trigger** on `federated_camera`, not application code. The camera upsert arrives
as one binary `COPY` plus one merge covering a whole target's inventory, so comparing in C# would
mean reading current state back first — a round trip per poll — and would silently miss any write
that did not come through that path. The trigger's `WHEN` clause is what makes it affordable:
every camera is updated every 30 seconds and almost none of those updates enter the function.

`federated_camera.status_changed_at` holds the current status's start instant separately, so
"Healthy since March" survives history retention dropping the transition that proves it.

---

## 8. Correlation

Correlation joins observations of the same `object_reference` (a plate, a person
reference, a track) across departments and vendors within a time window — the scenario in
`MODEL-3` §"Cross-System Correlation".

**Day-one implementation is windowed SQL over the hot PostgreSQL tier**, behind a
`CorrelationEngine` port. This is correct up to roughly 5k events/s, which is the design
point, and it is dramatically simpler to operate on bare metal than a Flink cluster.

Two invariants the implementation must hold, both from the specs:

- **Cross-camera identity is probabilistic.** A tracker ID is valid only within one
  camera (`MODEL-2`). Any cross-camera association carries a confidence score and is
  never surfaced as certainty in an API response or UI string.
- **Correlation rules are configuration, not code.** Time window, spatial radius,
  confidence floor, and required signal agreement are per-rule config, so operators tune
  without a deployment.

---

## 9. Security

| Control | Implementation |
|---|---|
| Credentials | Never in the registry or events. `credential_reference` resolves against a secret store (Vault; fallback pgcrypto with a KMS-wrapped DEK) |
| Transport | mTLS between platform services; TLS to VMS where the vendor supports it, explicitly flagged per target where it does not |
| Authorisation | RBAC, scoped by organization **and** geography as independent dimensions. Enforced in SQL and at the repository layer, not in the API handler alone. See `AUTHORIZATION.md` |
| Audit | Every configuration change is audit-logged **in the same transaction as the change** (§11). Every credential resolution is logged to `credential_access_log` — accessor, target, outcome — including failures |
| Isolation | Connector workers run as least-privilege service accounts, network-segmented toward their VMS subnets only |

The `credential_reference` indirection is load-bearing: a connector worker resolves a
secret at connect time and holds it in memory only. It is never written to the registry,
never logged, and never enters the event envelope.

The API surface has **one** route that returns secret material:
`GET /api/v1/vms/{id}/credential/resolve`, added for Model 2's AI worker, which connects
to camera RTSP streams directly rather than through a Model 3 adapter. It requires the
`credential.resolve` permission — held only by the `DETECTION_WORKER` machine role — is
scoped through the target like every other `/vms/{id}` route, and every call writes
`credential_access_log`. A resolution whose audit row cannot be written returns 503 with
the secret withheld: there is no path that discloses a credential without a log row. The
in-process connector worker still resolves through `ICredentialResolver` directly and never
touches this route. The worker→API hop for this call must be mTLS (or at minimum TLS) like
every other platform-internal call — it carries a plaintext camera password.

---

## 10. Schema lifecycle

**No running service applies migrations.** The API and the workers verify the schema at startup
and refuse to run against one that is missing or older than the build. Preparing the schema is an
operator action: someone applies `db/versions/vN.sql` with psql.

The reason is what happens at scale. Several API instances and a worker fleet restart together;
if each migrates on start, the schema moves underneath instances that have already begun serving.
An advisory lock serialises them but does not fix that — requests are still answered against a
schema mid-change, at a moment nobody chose.

```text
   psql -U trinetra -d trinetra -f db/versions/v1.sql          one controlled run
        |
        v
   schema at version N
        |
        v
   start binaries         each verifies N, or refuses and names the gap
```

The trade is explicit: deployment order now matters, and schema precedes binaries every time. A
host started against an older schema refuses rather than degrades, because a partially-migrated
schema fails *unpredictably* — some queries work, others reference columns that do not exist yet
— which presents as intermittent application bugs rather than as an ordering mistake.

Migrations are embedded in the Storage assembly, ordered by filename, applied exactly once, and
checksummed. An applied migration is never edited: installs that ran the old text would silently
diverge from those that ran the new one.

---

## 11. Audit atomicity

A configuration change and the record of it commit together or not at all.

They were previously written on **separate connections**, each committing independently, so a
process death or connection reset between them left the change applied and unrecorded. The record
that goes missing is, by definition, the one written closest to whatever caused the failure — so
"who repointed this NVR" had no answer for precisely the incident under investigation.

```text
   UnitOfWork.BeginAsync
        |
        +-- mutation          repository write, required UnitOfWork parameter
        |
        +-- audit row         UnitOfWork.AuditAsync, the only way to write one
        |
        v
   CommitAsync                or dispose, which rolls back
```

Enforced structurally rather than by convention: there is no injectable audit writer, and
repository write methods cannot open their own connection. A mutation that is not audited, or an
audit that is not atomic with its mutation, does not compile.

Dispose-without-commit rolls back, which is what makes an early return safe — a handler that
validates, mutates, then decides to answer 409 simply returns.

---

## 12. Retention

Partitions are created ahead of need and dropped on a schedule. `DROP PARTITION`, never `DELETE`:
at 100–400M rows/day a delete generates more WAL than the inserts it cleans up and leaves the
table bloated behind an autovacuum that never catches up.

| Table | Partitioning | Default retention |
|---|---|---|
| `federation_event` | Daily | 90 days |
| `connector_health` | Daily | 30 days |
| `config_audit`, `credential_access_log` | Monthly | 84 months |
| `connection_test`, dead letter | None — `DELETE` is correct at this volume | 30 / 90 days |

`connector_health` is partitioned because it is written every `StatusPollInterval` per target —
~5.8M rows/day at 2,000 VMS instances, second only to events.

Retention runs **once per day for the whole fleet**, claimed through `try_claim_daily_job`: every
instance attempts it and the database decides which one wins. No leader election, the same
reasoning as the connector leases in §4.

Three guards, because the operation is irreversible:

- A cutoff that is not in the past is refused **in SQL**, so it holds even when the caller is psql.
- Startup refuses any configured period below seven days.
- Every dropped partition is logged by name and recorded in `config_audit`.

Monthly audit partitions are dropped only once the *whole* month they cover is past the cutoff,
so an 84-month policy never removes an 83-month-old record.

> Retention periods are a **policy decision, not a technical one**. The defaults are sized for
> disk. CCTV metadata is subject to statutory retention that differs by state, department and
> purpose; confirm before going live. See `OPERATIONS.md` §3.

---

## 13. Event queries

`federation_event` grows 100–400M rows/day, so the query that serves it is constrained by shape
rather than by tuning:

1. `from` and `to` are **required** — missing either is 400, never a silent default.
2. The window is capped, the page size is capped.
3. Paging is **keyset**, never OFFSET: a deep offset scans everything it skips.
4. Scope is resolved to a concrete `uuid[]` before the query runs.

Points 3 and 4 are load-bearing in ways that are invisible until measured. On 400k events in one
partition:

| Written as | Time | Buffers | Rows discarded |
|---|---|---|---|
| Keyset as an `OR` chain | 83.6 ms | 4,270 | 378,400 |
| Keyset as a `ROW` comparison | 0.107 ms | — | 0 |
| Scope as a set-returning function in `IN` | 10.0 ms | 15,101 | ~9,200 per page |
| Scope as a `uuid[]` parameter | 1.6 ms | 372 | ~9,200 per page |

Measured with `EXPLAIN (ANALYZE, BUFFERS)` at a cursor roughly 21,000 rows into the partition.
The two rows are not directly comparable on buffers alone — the keyset pair was measured
unscoped, the scope pair with a department owning 1% of the partition.

The `OR` chain is *semantically identical* and cannot seek: the planner treats it as a filter and
re-reads every row before the cursor, so page N costs N pages of work — the exact deep-pagination
cost keyset was chosen to avoid. The set-returning function is opaque to the planner, which then
scans the time range and discards out-of-scope rows; at 1% scope that is ~99 rows read per row
returned.

Two query shapes are emitted rather than one, because `ROW(a, b) < ROW(NULL, NULL)` is NULL rather
than TRUE — a single parameterised query would return an empty first page.

**Boundary:** this serves the hot PostgreSQL window only. Free-text and wide historical search
need OpenSearch (§7); these endpoints get re-pointed at it rather than extended. Designing them
narrow is what makes that swap cheap.

---

## 14. Validating 80k with 100 cameras

The failures that matter at 80,000 cameras are invisible at 100 — lease contention,
partition skew, spool saturation, connection-pool exhaustion, cursor drift. So the
**VMS simulator is a first-class deliverable, not test scaffolding**.

`tools/Trinetra.VmsSimulator` presents N synthetic VMS endpoints speaking the real
adapter transports, with configurable:

- camera count per target, and target count
- event rate and burst shape
- response latency and jitter
- error injection: timeouts, 5xx, malformed payloads, auth expiry, duplicate event IDs,
  clock skew, out-of-order delivery

The load suite in `tests/Trinetra.LoadTests` runs the real connector workers against the simulator at
full 80k-camera inventory. **The scale target is a test that runs in CI, not a
projection.** A change that regresses the 80k simulation is a failing build.

---

## 15. Deliberate non-goals

- **No video.** Model 3 exposes stream *references*; it never proxies or transcodes.
- **No vendor logic outside adapters.** Core services, correlation, and API must never
  branch on vendor. This is the load-bearing constraint of the whole model.
- **No exactly-once claims.** See §5.
- **No camera-count-proportional work against vendor devices.** See §1.
- **No row-level security.** Authorisation is enforced in the application, in one place. RLS would
  require the caller's identity on the connection per request — which breaks under transaction
  pooling — and would split one model across two layers that can disagree.
- **No schema changes from a running service.** See §10.
- **No SQL in the API layer.** Every query lives in `Federation.Storage`, where the scope
  predicate that guards it lives too. Enforced by a banned-API analyzer, not by review: a query
  written next to its handler is a query whose `WHERE` clause the next person edits without
  realising it is the authorization boundary.
