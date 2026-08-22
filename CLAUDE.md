# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository State

**Model 3 (VMS Federation) is under active implementation. Models 1 and 2 are still specs only.**

Stack: **.NET 10 / ASP.NET Core minimal API**, PostgreSQL + PostGIS, Kafka, OpenSearch,
deployed on **on-prem bare metal** with systemd — no Kubernetes. Scale target is **80,000
cameras in production**, validated against a simulator; first-phase rollout is 100+ cameras.

`docs/ARCHITECTURE-MODEL-3.md` is the authoritative design for this work. Read it before
changing anything under `src/`; it records the decisions the code depends on, and code and
document change together.

### Commands

```bash
psql -U trinetra -d trinetra -f db/versions/v1.sql   # create the schema
dotnet build                              # whole solution
dotnet test                               # all tests
dotnet test tests/Trinetra.UnitTests      # one project
dotnet test --filter "FullyQualifiedName~TokenBucket"   # one test class
dotnet run --project src/Trinetra.Federation.Api        # API host
dotnet run --project src/Trinetra.Federation.Worker     # connector worker
dotnet list package --vulnerable --include-transitive   # must stay clean
```

Warnings are errors (`Directory.Build.props`), including analyzers — this code runs unattended
against hundreds of flaky third-party systems, so a nullability or async warning is a 3am
incident later. Test projects are exempt from naming/doc analyzers only. All package versions
are pinned centrally in `Directory.Packages.props`; floating versions are rejected by design.

### Project layout

| Project | Holds |
|---|---|
| `Federation.Core` | Event contract, `IVmsAdapter`, capabilities, domain models, error taxonomy. No I/O. |
| `Federation.Adapters` | Vendor implementations — **the only place vendor-specific code may exist** |
| `Federation.Runtime` | Lease manager, rate limiting, circuit breaking, connector workers |
| `Federation.Bus` | Kafka producer/consumer, envelope serialisation |
| `Federation.Storage` | Npgsql/Dapper repositories, lease store |
| `Federation.Api` | Read/serve side, department-scoped and audited |
| `Federation.Worker` | Connector worker host (systemd `Type=notify`) |
| `tools/Trinetra.VmsSimulator` | Synthetic VMS estate for scale validation — a deliverable, not scaffolding |

Database schema is raw SQL in `db/versions/`, one file per version. `v1.sql` builds a complete
database — tables, functions, views and the reference data authorisation resolves against;
`v1.1.sql` would hold only what v1.1 adds. `db/objects/` mirrors the same schema one file per
object, for review.

There is no migration tool, no `schema_version` table, and no startup check — **nothing in the
running system touches the schema.** A binary run against a stale database starts healthy and
fails at request time, so deployment ordering is operational discipline, not something enforced.

**A version file is never edited once applied anywhere.** Installs that ran the old text would
silently differ from those that ran the new one. Corrections go in the next version.

**No PostgreSQL extensions.** Coordinates are plain `DECIMAL(10,7)` with range CHECKs, and
`gen_random_uuid()` is core from PostgreSQL 13. PostGIS and pgcrypto were both verified
unnecessary and removed — spatial querying belongs to Model 1, and credential sealing is
application-side AES-256-GCM so the database never holds the key.

## The Three Models

The whole design is organised around three subsystems that share one metadata/event layer. Each has its own spec file; read the relevant one before implementing in that area.

| Model | Spec | Owns |
|---|---|---|
| 1 — Registry & GIS | `docs/MODEL-1-REGISTRY-GIS.md` | Camera registry, department/vendor ownership, geospatial coverage sectors, health & maintenance |
| 2 — Video Analytics | `docs/MODEL-2-VIDEO-METADATA-ANALYTICS.md` | FFmpeg decode → frame sampling → GPU inference → ANPR/person/vehicle observations → search index |
| 3 — VMS Federation | `docs/MODEL-3-VMS-FEDERATION-MIDDLEWARE.md` | Vendor adapters, event normalisation, cross-system correlation |

**Everything is in `docs/`.** The root holds only this file and `README.md`.

| Document | Read it when |
|---|---|
| `docs/ARCHITECTURE-MODEL-3.md` | Changing anything under `src/` — it records the decisions the code depends on |
| `docs/AUTHORIZATION.md` | Touching permissions, scope or audit |
| `docs/OPERATIONS.md` | Configuration, retention, troubleshooting a startup failure |
| `docs/DEPLOYMENT.md` | Bare-metal topology, systemd units, rolling upgrades |
| `docs/RBAC-LOGICAL-FLOW.md` | The authorization model itself, ahead of its implementation |
| `docs/{DEPARTMENT,GEOGRAPHY,CAMERA}-SCHEMA.md` | The entity models these follow |

`docs/TECHNICAL-DESIGN.md` is the integration view: logical architecture, data flows, the API surface (`/api/cameras`, `/api/gis`, `/api/observations`, `/api/events`, …), the common event contract, and a 10-phase build order starting with Model 1.

## Architectural Invariants

These constraints run across all three specs and should survive any implementation decision:

- **Adapters are the only place vendor-specific code lives.** Core services, dashboard, and analytics must never branch on VMS vendor. Adapters implement the contract in `MODEL-3` (`get_cameras`, `get_streams`, `get_events`, `subscribe_events`, …) plus **capability discovery**, because no VMS supports every capability.
- **The event bus is the seam.** Adapters and analytics publish into a common event schema; correlation, search, alerting, and GIS are all subscribers. New consumers must not require producer changes.
- **Video is the source; metadata is the product.** Search, correlation, and GIS operate on observation/event records, not on video. Video and snapshots are referenced (`stream_reference`, `snapshot_reference`, `video_reference`), never inlined.
- **Credentials are references, not fields.** `credential_reference` points at a secret-management system; camera/adapter credentials never live in registry metadata or the event schema.
- **Coverage is estimated, not measured.** Coverage sectors are derived from `azimuth` + `horizontal_fov` + `effective_range` via PostGIS. Present them as a planning aid — terrain and obstructions are not modelled.
- **Cross-camera identity is probabilistic.** A tracker ID is valid only within one camera. Cross-camera association (Re-ID, face similarity, ANPR chains) must carry a confidence score and must never be surfaced as certainty.
- **Every sensitive operation is RBAC-gated, department-scoped where applicable, and audit-logged.**

## Production Posture (supersedes the specs' prototype boundary)

`docs/TECHNICAL-DESIGN.md` §8 scopes this repo as a controlled-sample prototype. **That boundary
has been explicitly overridden by the project owner: Model 3 is being built production-ready
from day one.** Treat capacity, cybersecurity, privacy/legal compliance, retention and DR as
work items to be completed, not as deferred validation.

The two constraints that survive unchanged, because they are about honesty rather than scope:

- Cross-camera identity remains **probabilistic**, and confidence must never be presented as
  certainty in an API response or UI string.
- Coverage estimates remain **planning aids** — terrain and obstructions are not modelled.

## Target Demo Path

The end-to-end scenario the prototype must support, per `README.md`:

register camera → place on GIS → show coverage → connect sample VMS/feed → process video → generate ANPR/person/vehicle metadata → search metadata → correlate observations → plot movement on GIS → raise alert.

Judge new work by whether it advances this path.

## Non-Negotiables When Writing Model 3 Code

These are enforced by review, and most are enforced by the type system or schema too:

1. **Never issue work proportional to camera count against a vendor device.** Poll per VMS,
   not per camera — one `GetCamerasAsync()` returns a target's whole inventory. Per-camera
   polling is ~2,670 req/s at 80k cameras and no NVR survives it. This is why `IVmsAdapter`
   has no `GetCameraAsync(id)`.
2. **No vendor branching outside `Federation.Adapters`.** If adding a vendor needs a change
   elsewhere, the abstraction leaked and the change is wrong.
3. **Adapters implement no retries, rate limiting, or circuit breaking.** The runtime wraps
   every call; an adapter that retries internally hides failures from the breaker.
4. **Throw the specific adapter exception.** `AuthException` must not be retried — retrying a
   rejected credential across 80k cameras locks the integration account out estate-wide.
5. **Every sink is idempotent** on `(source_vms, source_event_id)`. Transport is at-least-once;
   nothing may assume exactly-once.
6. **`DateTimeOffset` everywhere**, never `DateTime`. Departments' clocks drift, and a naive
   timestamp silently corrupts every time-window correlation.
7. **Secrets are `CredentialReference` pointers.** Resolved at connect time, held in memory
   only, never logged or persisted. `Credential.ToString()` is redacted deliberately.
8. **Model 3 never touches video** — stream *references* only.
9. **No SQL in `Federation.Api`.** Every query lives in `Federation.Storage`, next to the scope
   predicate that guards it. Enforced by `BannedSymbols.txt` in the API project, not by review.
10. **A mutation and its audit row share one transaction.** Repository write methods take a
    required `UnitOfWork`; audit rows are writable only through it. There is no path that changes
    something without recording it, or that records it separately.
11. **Every scoped repository method takes a required `CallerContext`.** A query that omits
    scoping should fail to compile, not fail review.
12. **Organization and geography are independent scope dimensions**, ANDed. Never reuse one
    dimension's "unscoped" answer for the other. Unscoped is tracked per permission, never as a
    single flag.
13. **Changing a SQL function's signature means `DROP`, not `CREATE OR REPLACE`.** The latter
    creates an overload, so missed call sites keep binding to the old function with a green build.

## Documentation Conventions

The specs use fenced `text` blocks for ASCII architecture diagrams and flat field lists for data models. Match that style when extending them — keep diagrams ASCII, and keep field lists as bare names rather than language-specific type declarations, since the backend language is not yet fixed.
