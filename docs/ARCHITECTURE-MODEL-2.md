# Model 2 — Implementation Architecture & RFP Boundary Note

Implementation architecture for the Unified Viewing & Metadata Analytics layer described in
`MODEL-2-VIDEO-METADATA-ANALYTICS.md`. This document exists for the same reason
`ARCHITECTURE-MODEL-3.md` does for Model 3: to record the decision this system actually makes
and reconcile it explicitly against the RFP's own model-boundary language, so an evaluator reads
it here rather than has to infer it from code — see `docs/RFP-COMPLIANCE-GAP-ANALYSIS.md`
"Deviations from RFP model boundaries" §1, which this note closes.

---

## 1. The boundary, as built

The RFP's four-model reference architecture describes Model 2 and Model 3 as two independent
integration surfaces against departmental VMS/CCTV infrastructure: Model 2 "connects directly...
without introducing an intermediate middleware or federation layer," while Model 3 "does not
connect directly... the middleware layer is the common integration platform."

This system does not implement that literal separation. It implements a **hybrid**, and the RFP
itself explicitly permits this ("participating companies may propose a hybrid solution... or a
fully innovative architecture"). What's built:

```text
Departmental VMS / NVR / Camera
    |
    | vendor adapter (Federation.Adapters) — the ONLY place vendor-specific code exists
    v
Model 3 (Federation.Api) — camera & VMS inventory, RTSP/HTTP stream references, event feed
    |
    | GET /api/v1/vms  ->  GET /api/v1/vms/{id}/cameras  (read-only discovery, no credentials)
    | GET /api/v1/vms/{id}/credential/resolve             (one call, per target, per session)
    v
Model 2 (ai-worker) — opens the RTSP stream itself, decodes, runs inference, submits metadata
```

Model 2's ai-worker never speaks a vendor's native VMS API and never runs a second, parallel
discovery/credential-management path against departmental infrastructure. It discovers cameras,
their stream references, and their credentials entirely through Model 3's own read endpoints
(`ai-worker/camera_source.py`), then opens the RTSP stream directly and does its own decode +
inference + metadata submission (`ai-worker/pipeline.py`) — the media path itself never touches
Model 3; only camera discovery does.

## 2. Why this, not the literal RFP boundary

**One connector surface, not two.** Every vendor integration — auth quirks, pagination,
rate limits, the adapter contract in `docs/MODEL-3-VMS-FEDERATION-MIDDLEWARE.md` — is written
once, in `Federation.Adapters`, and audited once. A literal Model 2 with its own direct VMS
integration would duplicate that entire surface for a second, independent codebase, doubling the
vendor-specific maintenance burden and doubling the places a credential-handling or rate-limiting
bug could hide. `CLAUDE.md`'s own non-negotiable #2 ("No vendor branching outside
`Federation.Adapters`") would otherwise have to be relaxed for Model 2's sake, or violated by it.

**No second credential store.** Camera/VMS credentials are sealed once (AES-256-GCM,
`CredentialReference` pointers, `CLAUDE.md` invariant "Credentials are references, not fields")
and resolved through one audited path. A literal Model 2 boundary would need its own credential
acquisition and storage for the same devices — a second attack surface and a second place
`docs/AUTHORIZATION.md`'s guarantees would have to be independently re-established.

**Consistent with the RFP's own stated flexibility.** The RFP does not mandate literal
architectural separation as a compliance requirement — it explicitly invites a hybrid or "fully
innovative architecture" as an acceptable response. This is that response, made explicit rather
than left for an evaluator to reverse-engineer from the code.

## 3. What stays true to Model 2's own boundary regardless

- **Model 2 never touches a departmental VMS's write path.** Discovery is read-only
  (`GET /api/v1/vms/{id}/cameras`); nothing in the ai-worker path can reconfigure, control, or
  disable a camera or VMS. That authority stays entirely with Model 1 (registry) and Model 3's
  own connector-target management.
- **Model 2 owns the entire media and analytics path independently.** Once it has a stream
  reference, ai-worker's RTSP capture, frame sampling, GPU inference, and metadata submission
  (`POST /api/v1/detections`) are its own — Model 3 is never in that loop, never decodes a frame,
  never sees inference output before it lands in the registry's own metadata store. This matches
  `CLAUDE.md`'s "Model 3 never touches video" non-negotiable exactly.
- **No existing departmental system is modified, replaced, or requires any change.** Camera
  discovery and stream access are read operations against Model 3's own already-existing
  connector-target inventory; a department's VMS is queried exactly as it already is for Model 3's
  own registry/GIS purposes, with zero additional load, credential exposure, or configuration
  requirement placed on it by Model 2's existence. This is the "existing systems unaffected"
  claim the RFP's Model 2 deliverable asks for, and it holds regardless of the boundary question
  above — Model 2 adds a consumer of data Model 3 was already collecting, not a second integration
  against the department's own infrastructure.

## 4. Where this is enforced, not just documented

- `ai-worker/camera_source.py`'s own module docstring states this discovery path and cites the
  exact two endpoints it calls.
- `docs/MODEL-2-VIDEO-METADATA-ANALYTICS.md`'s "Input Boundary with Model 3" section already
  specifies Model 2 consumes stream references *from* Model 3 — this document adds the explicit
  RFP-language reconciliation that section doesn't itself spell out.
- `CLAUDE.md` non-negotiables #1–#4, #8 (poll-per-VMS, no vendor branching outside adapters,
  adapters retry nothing, throw specific exceptions, Model 3 never touches video) all apply
  transitively to every camera Model 2 discovers, since discovery is entirely Model 3's own code
  path.
