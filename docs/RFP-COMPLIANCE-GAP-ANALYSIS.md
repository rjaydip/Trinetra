# RFP Compliance & Gap Analysis — Models 1, 2, 3

Status: point-in-time compliance snapshot against the State CCTV integration RFP's four
reference models. Model 4 (Central VMS) is out of scope for this project — not evaluated here,
correctly excluded per `CLAUDE.md`. Grounded in a read of actual code, database schema, and
docs as of 2026-09-14, not from the specs alone; every line cites the source it comes from.

This is a snapshot, not a contract — re-verify against current code before using it to answer
an evaluator, since implementation status changes under this document.

---

## Model 1 — Centralised CCTV Registry & GIS Mapping

| Requirement | Status | Evidence |
|---|---|---|
| Bulk import | ✅ Implemented | `POST /api/v1/cameras/bulk-import`; `camera.import` permission (v1.6); frontend `BulkImportPage.tsx` |
| Manual + API onboarding | ✅ Implemented | Camera CRUD endpoints; `NewCameraPage.tsx`, `CameraForm.tsx` |
| GIS map — department/camera-type/status/coverage layers | ✅ Implemented | `GET /api/v1/gis/cameras` (GeoJSON, bbox-filtered); frontend `MapPage.tsx`, `CameraMap.tsx`, `MapFilters.tsx` |
| Camera health monitoring | ✅ Implemented | `CameraHealthEndpoints.cs`, `camera_health_history` table (v1.6); `CameraHealthHistory.tsx` |
| Maintenance-status monitoring | ✅ Implemented | `CameraMaintenanceRepository`, `maintenance_records` table (v1.6) |
| Gap-analysis reports for uncovered zones | ❌ Not started | `GET /api/v1/gis/gaps` is a hard 501 (`GisEndpoints.cs:79-84,232` — "Returns 501 until then; the route exists so the contract is stable"). Blocked on PostGIS, which is deliberately not installed (`CLAUDE.md`: "PostGIS is reserved for the later coverage-gap-analysis slice"). This is a scope decision, not an oversight. |
| Ageing-infrastructure reporting | ❌ No code footprint | No "ageing"/"aging" logic anywhere in code or docs beyond this analysis. `ReportsPage.tsx` / `CoverageSummary.tsx` exist but cover coverage summary, not equipment age. |
| Role-based search/filter/export | ⚠️ Partial | Search/filter implemented (`CameraFilters.tsx`, scoped list endpoints). No CSV/export endpoint or UI control found anywhere. |
| Metadata audit trails | ✅ Implemented | `config_audit` (partitioned), `UnitOfWork.AuditAsync`; `BulkImportAuditFidelityTests.cs` confirms bulk-import audit fidelity specifically |
| Deliverable: registry API documentation | ✅ Implemented | `/openapi/v1.json` + `/scalar` mapped in every environment (`CLAUDE.md`, 2026-09-09) |
| Deliverable: sample gap-analysis report | ❌ Blocked | Same PostGIS dependency as above |

---

## Model 2 — Unified Viewing & Metadata Analytics

| Requirement | Status | Evidence |
|---|---|---|
| Feed aggregation via RTSP/ONVIF/vendor APIs | ⚠️ Deviation | See "Deviations from RFP model boundaries" §1 — ai-worker does not connect directly to departmental VMS; it discovers cameras via `GET /api/v1/vms/{id}/cameras` (Model 3's `Federation.Api`), then opens RTSP itself (`capture/rtsp_source.py`) |
| ANPR-based metadata generation | ✅ Implemented | `ai-worker/pipeline.py`, `backends/plate/*`, `backends/ocr/*` |
| Event tagging | ⚠️ Partial | `DetectionEventTypes` is a closed set (`ANPR_DETECTED`, `VEHICLE_DETECTED`) in `DetectionEndpoints.cs` — structural tagging exists; no free-form/operator tagging UI found |
| Camera-wise indexing | ✅ Implemented | `DetectionRepository`, ingest keyed by camera (`ResolveCameraAsync`), search supports camera filter |
| Searchable vehicle-movement records | ✅ Implemented | `POST /api/v1/detections` (`DetectionEndpoints.cs:88-189`, idempotent on caller-supplied id, 8 MB cap) + `GET /api/v1/detections` search (`:119,318`); frontend `DetectionsPage.tsx`. This closed a gap flagged as missing in an earlier review pass of this codebase — confirm it is still live before relying on the claim. |
| Configurable video walls / multi-camera grid views | ❌ Not started | No video-wall/grid-view component found anywhere in `frontend/src` |
| Alerts for tagged events/vehicles of interest | ✅ Implemented | `WatchlistEndpoints.cs`, `watchlist_entry`/`watchlist_alert` tables, `WatchlistPage.tsx`, `scripts/add-watchlist-plate.sh` |
| Deliverable: unified viewer connected to ≥2 systems | ⚠️ Partial | VMS management UI (`VmsPage.tsx`, `DiscoveryPage.tsx`) exists for connecting multiple VMS targets; no single "unified viewer" grid/playback surface (same gap as video walls) |
| Deliverable: ANPR demo on live/recorded feeds | ✅ Capability present | ai-worker pipeline supports live RTSP; per project memory, a real-camera e2e test was in progress as of 2026-08-31 — verify current status before demoing |
| Deliverable: searchable metadata dashboard | ✅ Implemented | `DetectionsPage.tsx` + search endpoint above |
| Deliverable: architecture note (existing systems unaffected) | ⚠️ Partial | `docs/ARCHITECTURE-MODEL-3.md` covers this for Model 3; no equivalent note scoped to Model 2 specifically |

---

## Model 3 — VMS Federation & Middleware Integration

| Requirement | Status | Evidence |
|---|---|---|
| Adapter/plugin architecture | ✅ Implemented | `Federation.Adapters` — `CLAUDE.md`: "the only place vendor-specific code may exist" |
| Metadata exchange bus | ✅ Implemented | `Federation.Bus` (Kafka producer/consumer, envelope serialisation) |
| Cross-system event-correlation engine | ❌ **Not started — spec only, no code at all** | `docs/MODEL-3-VMS-FEDERATION-MIDDLEWARE.md` and `docs/ARCHITECTURE-MODEL-3.md` §8 describe an `ICorrelationEngine` port / `SqlCorrelationEngine`; a repo search for `*Correlation*` under `src/` returns nothing |
| Unified workflow and alert dashboard | ⚠️ Partial | `EventsPage.tsx`, `WatchlistPage.tsx`, `WorkerHealthPage.tsx` exist as separate admin pages; no single unified cross-system workflow dashboard tying correlation output together (consistent with the correlation engine itself not existing) |
| Extensible connector framework | ✅ Implemented | `Federation.Runtime` (lease manager, rate limiter, circuit breaker), `LeaseManager.cs`/`LeaseStore.cs` for dynamic multi-worker target assignment |
| Deliverable: middleware demo federating ≥2 systems | ✅ Capability present | Multiple adapters + `VmsManagement.tsx`/`DiscoveryPage.tsx`; `tools/Trinetra.VmsSimulator` for scale validation |
| Deliverable: unified event-correlation dashboard | ❌ Not started | Depends on the missing correlation engine above |
| Deliverable: adapter/plugin architecture documentation | ✅ Implemented | `docs/ARCHITECTURE-MODEL-3.md`, `docs/MODEL-3-VMS-FEDERATION-MIDDLEWARE.md` |
| Deliverable: sample federated analytics report | ⚠️ Partial | `ReportsPage.tsx`/`CoverageSummary.tsx` produce coverage-style reports; nothing framed as cross-VMS federated analytics |

---

## Deviations from RFP model boundaries

1. **Model 2 does not connect directly to departmental VMS, contradicting its RFP definition.**
   The RFP explicitly distinguishes Model 2 ("connects directly... without introducing an
   intermediate middleware or federation layer") from Model 3 ("does not connect directly... the
   middleware layer is the common integration platform"). The actual system routes Model 2
   (ai-worker) through Model 3: camera discovery is `GET /api/v1/vms/{id}/cameras`
   (`Federation.Api`), i.e., ai-worker never talks to a departmental VMS's own API — only to RTSP
   streams whose existence and address it learned from the federation layer. This is
   architecturally a **hybrid**, not literal Model 2 + Model 3 side-by-side. It may be a
   deliberate, reasonable simplification (one connector surface instead of two, avoiding
   duplicated VMS integration logic), but as written it does not satisfy the RFP's stated Model 2
   boundary language and should be called out explicitly to evaluators rather than left implicit.
   The RFP itself permits this ("participating companies may propose a hybrid solution... or a
   fully innovative architecture"), so the fix here is disclosure, not necessarily rearchitecture.
2. **Cross-system correlation — the definitional feature of Model 3 — has no implementation**,
   only a documented port (`ICorrelationEngine`) and design intent. Everything else under Model 3
   (adapters, bus, connector framework) is real; this piece is the one still entirely on paper.
3. **PostGIS-dependent features are structurally deferred**, not partially done: gap-analysis
   (Model 1) and any coverage-gap-based Model 1 deliverable are blocked on a database extension
   `CLAUDE.md` says is deliberately not installed. This is a scope decision, not an oversight, but
   it means two RFP deliverables (gap-analysis report, uncovered-zone reporting) cannot ship
   without reversing that decision.
4. **Video-wall/grid-view viewing surface is entirely absent** — despite "Unified Viewing" being
   Model 2's namesake, there is no multi-camera grid/wall UI; the frontend covers registry, map,
   detections, events, VMS management, and admin, but not live multi-feed viewing itself.
5. **Export (Model 1) and ageing-infrastructure reporting (Model 1) have no code footprint at
   all** — not partial, not stubbed, no trace found.

---

## Priority recommendation

Three gaps carry the most evaluation risk if left unaddressed:

1. **Cross-system correlation engine (Model 3)** — the single largest gap; it's the defining
   Model 3 deliverable and doesn't exist as code.
2. **GIS gap-analysis / uncovered-zone reporting (Model 1)** — deliberately deferred behind the
   PostGIS decision; two RFP deliverables can't ship without reversing it.
3. **Video-wall/multi-camera grid viewing (Model 2)** — "Unified Viewing" is the model's namesake
   and there is no live multi-feed viewing UI at all.

Lower-cost gaps worth closing opportunistically: CSV export (Model 1), ageing-infrastructure
reporting (Model 1), free-form event tagging (Model 2), a Model-2-scoped architecture note
mirroring `docs/ARCHITECTURE-MODEL-3.md`.
