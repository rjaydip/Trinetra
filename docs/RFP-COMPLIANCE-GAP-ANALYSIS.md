# RFP Compliance & Gap Analysis — Models 1, 2, 3

Status: point-in-time compliance snapshot against the State CCTV integration RFP's four
reference models. Model 4 (Central VMS) is out of scope for this project — not evaluated here,
correctly excluded per `CLAUDE.md`. Grounded in a read of actual code, database schema, and
docs as of 2026-09-15, not from the specs alone; every line cites the source it comes from.

This is a snapshot, not a contract — re-verify against current code before using it to answer
an evaluator, since implementation status changes under this document.

---

## Model 1 — Centralised CCTV Registry & GIS Mapping

| Requirement | Status | Evidence |
|---|---|---|
| Bulk import | ✅ Implemented | `POST /api/v1/cameras/bulk-import`; `camera.import` permission (v1.6); frontend `BulkImportPage.tsx` — now also accepts CSV (not just JSON), with organization-unit/geographic-area/credential columns by *name*, resolved to IDs at parse time (`import.ts`) |
| Manual + API onboarding | ✅ Implemented | Camera CRUD endpoints; `NewCameraPage.tsx`, `CameraForm.tsx` |
| GIS map — department/camera-type/status/coverage layers | ✅ Implemented | `GET /api/v1/gis/cameras` (GeoJSON, bbox-filtered); frontend `MapPage.tsx`, `CameraMap.tsx`, `MapFilters.tsx`, plus a "Find on map" camera search and a "Go to a location" address/place search (`MapSearchBox.tsx`, `LocationSearchBox.tsx`, OpenStreetMap Nominatim) |
| Camera health monitoring | ✅ Implemented | `CameraHealthEndpoints.cs`, `camera_health_history` table (v1.6); `CameraHealthHistory.tsx` |
| Maintenance-status monitoring | ✅ Implemented | `CameraMaintenanceRepository`, `maintenance_records` table (v1.6) |
| Gap-analysis reports for uncovered zones | ✅ Implemented | `GET /api/v1/gis/gaps` (v1.19) — PostGIS `ST_Difference`s the C#-computed coverage sectors against a surveyed `geographic_areas.boundary`; 404 "no boundary set" when the target area has none. Boundary data loads via the admin-only `POST /geographic-areas/bulk-import-boundaries` — now with a dedicated admin UI page (`Admin → Geographic boundaries`, `BoundaryImportPage.tsx`: click-to-draw-on-map, paste GeoJSON/WKT, or bulk JSON upload), not curl-only. Surfaced in `ReportsPage.tsx`'s coverage-gap-analysis panel. |
| Ageing-infrastructure reporting | ✅ Implemented | `GET /api/v1/cameras/reports/ageing-infrastructure` (v1.26 — no schema change needed, reads `cameras.installation_date`): in-scope live cameras bucketed by installation age (`under_3`/`3_to_5`/`5_to_10`/`10_plus`/`unknown`) plus the oldest N with a known date, for replacement prioritisation. `CameraRepository.GetAgeingInfrastructureAsync`; surfaced in `ReportsPage.tsx`'s "Ageing infrastructure" panel. |
| Role-based search/filter/export | ✅ Implemented | Search/filter (`CameraFilters.tsx`, scoped list endpoints) plus an "Export CSV" action on `RegistryPage.tsx` — exports every camera matching the *current* committed search/filter (not just the visible page), in the same name-based CSV shape the importer reads, scoped by whatever permissions/filters already applied. No separate export endpoint: paginates the existing scoped `GET /cameras` client-side. |
| Metadata audit trails | ✅ Implemented | `config_audit` (partitioned), `UnitOfWork.AuditAsync`; `BulkImportAuditFidelityTests.cs` confirms bulk-import audit fidelity specifically |
| Deliverable: registry API documentation | ✅ Implemented | `/openapi/v1.json` + `/scalar` mapped in every environment (`CLAUDE.md`, 2026-09-09) |
| Deliverable: sample gap-analysis report | ✅ Implemented | Same `GET /gis/gaps` above, now real |

---

## Model 2 — Unified Viewing & Metadata Analytics

| Requirement | Status | Evidence |
|---|---|---|
| Feed aggregation via RTSP/ONVIF/vendor APIs | ⚠️ Deviation, now disclosed | See "Deviations from RFP model boundaries" §1 and `docs/ARCHITECTURE-MODEL-2.md` — ai-worker does not connect directly to departmental VMS; it discovers cameras via `GET /api/v1/vms/{id}/cameras` (Model 3's `Federation.Api`), then opens RTSP itself (`ai-worker/camera_source.py`, `capture/rtsp_source.py`) |
| ANPR-based metadata generation | ✅ Implemented | `ai-worker/pipeline.py`, `backends/plate/*`, `backends/ocr/*` |
| Event tagging | ✅ Implemented | `DetectionEventTypes` stays a closed machine classification (`ANPR_DETECTED`, `VEHICLE_DETECTED` — a deliberate design choice, finding 15-L2, not a gap) *plus* free-form operator tagging, additive and separate: `POST`/`DELETE /api/v1/detections/{eventId}/tags` (v1.26, `detection_tag` table, denormalised org/geo scope), surfaced as tag chips + an add/remove UI in `DetectionsPage.tsx` (`DetectionTags.tsx`), gated on `observation.write` |
| Camera-wise indexing | ✅ Implemented | `DetectionRepository`, ingest keyed by camera (`ResolveCameraAsync`), search supports camera filter |
| Searchable vehicle-movement records | ✅ Implemented | `POST /api/v1/detections` (`DetectionEndpoints.cs`, idempotent on caller-supplied id, 8 MB cap) + `GET /api/v1/detections` search, now also returning each result's operator tags; frontend `DetectionsPage.tsx`. Confirm the AI-worker e2e path is still live before demoing (per project memory, a real-camera test was in progress as of 2026-08-31). |
| Configurable video walls / multi-camera grid views | ✅ Implemented | `frontend/src/features/videowall/` (`VideoWallPage.tsx`, `LiveVideoTile.tsx`) — live multi-camera grid, HLS via hls.js by default, with per-camera native HLS/WebRTC (WHEP) stream-source support (v1.25) bypassing MediaMTX when a camera exposes its own endpoint |
| Alerts for tagged events/vehicles of interest | ✅ Implemented | `WatchlistEndpoints.cs`, `watchlist_entry`/`watchlist_alert` tables, `WatchlistPage.tsx`, `scripts/add-watchlist-plate.sh`, plus a recent-alerts feed on the new `/correlation` dashboard |
| Deliverable: unified viewer connected to ≥2 systems | ✅ Implemented | VMS management UI (`VmsPage.tsx`, `DiscoveryPage.tsx`) for connecting multiple VMS targets, plus the video-wall grid above as the actual unified live-viewing surface |
| Deliverable: ANPR demo on live/recorded feeds | ✅ Capability present | ai-worker pipeline supports live RTSP; verify current e2e status before demoing (see above) |
| Deliverable: searchable metadata dashboard | ✅ Implemented | `DetectionsPage.tsx` + search endpoint above, now with operator tags |
| Deliverable: architecture note (existing systems unaffected) | ✅ Implemented | `docs/ARCHITECTURE-MODEL-2.md` — new: reconciles the hybrid Model 2/3 boundary against the RFP's own language and states explicitly why no existing departmental system is modified, replaced, or requires any change |

---

## Model 3 — VMS Federation & Middleware Integration

| Requirement | Status | Evidence |
|---|---|---|
| Adapter/plugin architecture | ✅ Implemented | `Federation.Adapters` — `CLAUDE.md`: "the only place vendor-specific code may exist" |
| Metadata exchange bus | ✅ Implemented | `Federation.Bus` (Kafka producer/consumer, envelope serialisation) |
| Cross-system event-correlation engine | ✅ Implemented | `SqlCorrelationEngine` (`Federation.Storage`) implements the `ICorrelationEngine` port day-one over SQL, not Kafka (v1.18, per `ARCHITECTURE-MODEL-3.md` §8's explicit day-one/day-two split); `CorrelationRunner` (`Federation.Worker`) polls and upserts `correlation_group`/`correlation_group_member`; `GET /api/v1/correlation/groups[/{id}]` (`correlation.read`) is read-only, rules are system-seeded. |
| Unified workflow and alert dashboard | ✅ Implemented | New `/correlation` page (`CorrelationDashboardPage.tsx`) — cross-camera correlation groups (expandable to member events, lazily fetched per group), a federated cross-VMS analytics summary, a recent-watchlist-alerts feed, and a recent-events feed, each linking out to its own full page for deeper filtering. Gated on `correlation.read`; alert/event panels additionally gated on `alert.read`/`event.read`. |
| Extensible connector framework | ✅ Implemented | `Federation.Runtime` (lease manager, rate limiter, circuit breaker), `LeaseManager.cs`/`LeaseStore.cs` for dynamic multi-worker target assignment |
| Deliverable: middleware demo federating ≥2 systems | ✅ Capability present | Multiple adapters + `VmsManagement.tsx`/`DiscoveryPage.tsx`; `tools/Trinetra.VmsSimulator` for scale validation |
| Deliverable: unified event-correlation dashboard | ✅ Implemented | Same `/correlation` page above — the correlation-groups panel *is* this deliverable |
| Deliverable: adapter/plugin architecture documentation | ✅ Implemented | `docs/ARCHITECTURE-MODEL-3.md`, `docs/MODEL-3-VMS-FEDERATION-MIDDLEWARE.md` |
| Deliverable: sample federated analytics report | ✅ Implemented | The `/correlation` dashboard's "Federated cross-VMS analytics" section — total correlation groups, total correlated member events, average possible-match confidence, and a breakdown by correlation rule, computed client-side from the same `GET /correlation/groups` data (no new endpoint) |

---

## Deviations from RFP model boundaries

1. **Model 2 does not connect directly to departmental VMS, contradicting its RFP definition —
   now explicitly disclosed, not just implicit in the code.** The RFP explicitly distinguishes
   Model 2 ("connects directly... without introducing an intermediate middleware or federation
   layer") from Model 3 ("does not connect directly... the middleware layer is the common
   integration platform"). The actual system routes Model 2 (ai-worker) through Model 3: camera
   discovery is `GET /api/v1/vms/{id}/cameras` (`Federation.Api`), i.e., ai-worker never talks to
   a departmental VMS's own API — only to RTSP streams whose existence and address it learned
   from the federation layer. This is architecturally a **hybrid**, not literal Model 2 + Model 3
   side-by-side. The RFP itself permits this ("participating companies may propose a hybrid
   solution... or a fully innovative architecture"). **Resolved as a disclosure item**:
   `docs/ARCHITECTURE-MODEL-2.md` (new) states this plainly, explains why (one connector surface,
   one credential path, `CLAUDE.md` non-negotiables), and confirms no existing departmental
   system is modified, replaced, or requires any change as a result.
2. ~~Cross-system correlation — the definitional feature of Model 3 — has no implementation~~
   **Resolved (v1.18 engine, this pass's `/correlation` dashboard).** `SqlCorrelationEngine` +
   `CorrelationRunner` + `GET /correlation/groups` were already real; the unified dashboard
   surfacing that output is now also built.
3. ~~PostGIS-dependent features are structurally deferred~~ **Resolved (v1.19).** PostGIS is
   installed; `GET /gis/gaps` is real, differencing computed coverage sectors against a surveyed
   `geographic_areas.boundary`, with an admin UI (draw/paste/bulk-upload) to load one.
4. ~~Video-wall/grid-view viewing surface is entirely absent~~ **Resolved.**
   `frontend/src/features/videowall/` is a real live multi-camera grid, further extended this
   pass with native HLS/WebRTC per-camera stream sourcing (v1.25).
5. ~~Export (Model 1) and ageing-infrastructure reporting (Model 1) have no code footprint at
   all~~ **Resolved.** Both implemented — see the Model 1 table above.

---

## Priority recommendation

Every gap this document has tracked across all three RFP models is now closed — engine,
dashboard, disclosure, and reporting deliverables alike. Nothing outstanding is tracked here as
of 2026-09-15; the standing caveat is the one at the top of this document: **re-verify against
current code before answering an evaluator with it**, since this is a snapshot, not a contract,
and implementation status changes under it.

The two things worth re-confirming live rather than trusting this document blindly:

1. **The AI-worker real-camera e2e path** (Model 2's ANPR demo deliverable) — per project memory,
   a live test was in progress as of 2026-08-31; confirm it still runs end to end before
   demoing.
2. **`docs/ARCHITECTURE-MODEL-2.md`'s disclosure framing** — this is the right technical answer
   to the RFP boundary question, but whether an evaluator *accepts* a hybrid architecture as
   compliant (versus scoring it as a deviation regardless of disclosure) is a procurement
   judgment call, not something this document can settle on its own.
