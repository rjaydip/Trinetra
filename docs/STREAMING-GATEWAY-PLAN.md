# Live Streaming Gateway — Plan (closes the RFP's "video wall" gap)

Status: **implemented (G1-G5), not yet deployed anywhere.** G6 (metrics) is the one open item —
see §5. **Revised after initial implementation**: G4's HLS proxy was originally built as a
separate nginx layer in front of MediaMTX; it has since been folded directly into
`Federation.Api` (`GET /api/v1/streams/{cameraId}/{*hlsPath}`, `StreamSessionEndpoints.cs`) so a
deployment needs only two processes — the API and MediaMTX — not three. `GET
/api/v1/streams/validate` is kept only for a deployment that still prefers an external reverse
proxy; it is not part of the default path. See §2 and §3/G4 for the current shape. Originally
written to close the gap identified in
`docs/RFP-COMPLIANCE-GAP-ANALYSIS.md` — the RFP's Model 2 requires "configurable video walls and
multi-camera grid views" and a "unified viewer connected to sample feeds from at least two
different systems," both of which mean **actual video**, not the status-only tiles the video
wall currently shows. This document is grounded in the actual codebase (`docs/ARCHITECTURE-MODEL-3.md`,
`docs/AUTHORIZATION.md`, `ai-worker/capture/`, `frontend/src/features/videowall/`) as of
2026-09-14, not written from the spec alone.

---

## 0. What already exists, and the one sentence that sets scope

`docs/ARCHITECTURE-MODEL-3.md` §1 states the scale principle for the whole platform: *"Model 3
does not decode, transcode, or move video. That is Model 2's job."* This gateway **is** that job
— the gateway process and MediaMTX belong entirely in Model 2's territory, alongside
`ai-worker/`, not inside `Federation.Worker`/`Federation.Runtime`. The two new credential-resolve
endpoints (G1, G2 below) are the one exception: they correctly stay in the shared
`Federation.Api` control plane, exactly where the existing VMS `credential/resolve` endpoint
already lives despite also existing solely to serve Model 2's `ai-worker` — the control-plane
API is shared infrastructure across all three models, not Model-3-owned.

The one existing precedent to build on, not reinvent: `GET /api/v1/vms/{id}/credential/resolve`
(`docs/ARCHITECTURE-MODEL-3.md` §9, `docs/AUTHORIZATION.md:275-276`) already exists specifically
because ai-worker "connects to camera RTSP streams directly rather than through a Model 3
adapter" — gated on `credential.resolve`, held only by the `DETECTION_WORKER` machine role,
every call audited to `credential_access_log`, 503-not-200 if the audit row can't be written.
This gateway needs the exact same shape of access, for the exact same reason (a process outside
Model 3 opening an RTSP connection directly), and should reuse this pattern rather than invent a
second credential-disclosure path.

**Gap found while scoping this**: that endpoint resolves a *VMS target's* credential. A
manually-registered camera (no `vmsId`) carries its own `credential_reference`
(`CameraCredentialEndpoints.cs`), and there is currently **no** `GET
/api/v1/cameras/{id}/credential/resolve` symmetric to the VMS one — `CameraCredentialEndpoints`
today only exposes `/status` (existence-check, never the secret). This plan needs that new
endpoint for standalone cameras; VMS-managed cameras reuse the existing VMS-level resolve.

---

## 1. Protocol choice: HLS via MediaMTX, not a hand-built WebRTC stack

**Recommendation: HLS output, served by [MediaMTX](https://github.com/bluenviron/mediamtx)
(formerly `rtsp-simple-server`) — a single static Go binary, not something we write.**

Reasoning, weighed against the constraints this project actually has:

- **Bare metal, no Kubernetes** (`CLAUDE.md`). MediaMTX is one binary + one YAML config file,
  runs directly under systemd exactly like `Federation.Worker` already does
  (`deploy/systemd/trinetra-worker@.service` is the template to copy). No cluster, no sidecar,
  no SFU to operate.
- **On-demand pull, not always-on relay.** MediaMTX supports "run on demand" sources — it only
  opens the RTSP connection to a camera when a viewer actually requests that stream, and closes
  it when the last viewer leaves. This is the sizing principle from §1 applied to video: the
  unit of scale for this gateway is **concurrent viewers of a camera**, not the camera count. A
  100-camera rollout with a handful of operators watching a 3x3 wall is a tiny fraction of
  cameras actively streaming at once, not 100 simultaneous transcodes.
- **Remux, not re-encode, for the common case.** Almost every IP camera already emits H.264,
  which HLS (via `.ts`/fMP4 segments) can carry without re-encoding — MediaMTX remuxes
  (cheap, CPU-light) rather than transcodes (expensive) whenever the source codec is
  HLS-compatible already. Re-encoding is only needed for an unusual codec, and can be added
  later as a per-camera opt-in, not a default cost paid by every stream.
- **Bandwidth, not just CPU, is the actual per-viewer cost.** Even remuxed (not re-encoded)
  H.264 HLS is still full-bitrate video — a handful of operators each watching a 3x3 or 4x4 grid
  is `viewers × tiles × per-stream bitrate` of egress from the MediaMTX host: at ~2-4 Mbps per
  1080p stream, one operator watching a 4x4 grid alone is 32-64 Mbps. That's a non-issue at
  "100+ camera first-phase rollout," but it is the real constraint well before 80,000 cameras are
  reached, because — consistent with the concurrent-viewers framing above — it scales with
  viewer count, not camera count. MediaMTX's per-stream/per-reader bitrate belongs in G6's
  capacity-planning metrics from day one; a lower-resolution HLS rendition (separate from the
  full-resolution feed `ai-worker` captures for AI inference) is a low-effort win worth
  considering if grid views turn out to be operator-dense.
- **WebRTC is the wrong tradeoff here.** Its latency advantage (sub-second vs. HLS's few
  seconds) matters for interactive/control use cases; it does not matter for a monitoring grid
  where a 2-6 second delay is unnoticed. What WebRTC costs in exchange — STUN/TURN, ICE
  negotiation, a stateful SFU, much harder to reason about on bare metal without a cluster — is
  not worth paying for a benefit nobody needs. MediaMTX can also emit WebRTC from the same
  source with no extra work if a future requirement genuinely needs sub-second latency (e.g. PTZ
  control) — this is a config flag, not an architecture change, so it does not need deciding now.

---

## 2. Shape of the system

```
Browser (VideoWallPage tile)
   │  1. GET /api/v1/streams/{cameraId}/session   (Federation.Api, camera.read + scope)
   ▼
Federation.Api  ── issues a short-lived signed stream token, never the RTSP URL/credential
   │
   │  2. GET /api/v1/streams/{cameraId}/{*hlsPath}, Authorization: Bearer <token>
   ▼
Federation.Api (same process)  ── validates the token, checks it authorizes THIS cameraId,
   │                                proxies the request straight to MediaMTX
   ▼
MediaMTX (systemd service, bare metal, loopback-only)
   │  on first viewer: pulls RTSP from the camera, remuxes to HLS
   │  its own path config was pre-populated by provision_paths.py (§7), which tries
   │  GET /api/v1/cameras/{id}/credential/resolve first and only falls back to
   │  GET /api/v1/vms/{vmsId}/credential/resolve if the camera has no credential of its own —
   │  there is no automatic server-side fallback between the two (confirmed during G1)
   ▼
Camera (RTSP source)
```

The credential never reaches the browser and never reaches MediaMTX's config file in plaintext
at rest — resolved at connect time into MediaMTX's on-demand source command, held in memory the
same way `Federation.Worker`'s adapters already hold VMS credentials
(`docs/ARCHITECTURE-MODEL-3.md`'s "resolved at connect time, held in memory only" rule).

**Why `Federation.Api` proxies directly, rather than a separate reverse-proxy process in front of
MediaMTX (the original design):** MediaMTX itself still has no concept of this platform's RBAC —
org/geography scoping, `camera.read`, audit logging — so *something* still has to validate every
stream request before a byte moves. The original plan put that check in a standalone nginx
`auth_request` layer, reasoning that streaming byte-volume HLS traffic through the same
process/thread pool as ordinary JSON CRUD risked starving normal API latency under viewer load.
That tradeoff is deliberately accepted now in exchange for **one fewer process to deploy and
operate** — two processes (API + MediaMTX) instead of three (API + nginx + MediaMTX). If viewer
load ever actually degrades ordinary API latency in practice, reintroducing a dedicated proxy in
front of this same validation logic is a config change, not a rewrite — `GET
/api/v1/streams/validate` already exists for exactly that path and is exercised by nothing else,
so it hasn't rotted even though it isn't the default today.

---

## 3. New pieces, concretely

| # | Item | Where |
|---|---|---|
| G1 | `GET /api/v1/cameras/{id}/credential/resolve` — new, symmetric to the VMS one. Same shape: new permission `camera.credential.resolve`, scoped through the camera the same way `CameraCredentialEndpoints` already scopes, every call audited to `credential_access_log`, 503-not-200 on an audit-write failure, and the caller→API hop runs over mTLS (or at minimum TLS) — the same transport requirement `ARCHITECTURE-MODEL-3.md` §9 already places on this route class, inherited here, not a new decision, and not something a bare-metal deployment gets to skip internally. Held only by a **new** machine role, `STREAMING_GATEWAY` — see the firm decision below, not `DETECTION_WORKER`. | `Federation.Api`, `Federation.Storage` |
| G2 | `GET /api/v1/streams/{cameraId}/session` — after the normal `camera.read` + org/geo scope check, mints a short-lived JWT (a few minutes' TTL) scoped to one camera id, issued through the **existing** `JwtTokenService` signing-key ring (`Auth:Jwt:SigningKeys`) rather than a new HMAC scheme — reuses this platform's one existing signed-token mechanism and key-rotation story instead of standing up a second one. Claim set is minimal (camera id, expiry — no user/role claims a client needs to read) and carries a distinct audience/purpose claim so a stream token can never be replayed as a bearer token against the rest of the API. | `Federation.Api` (new `StreamSessionEndpoints.cs`) |
| G3 | MediaMTX deployment: systemd unit + config template, `path`-per-camera on-demand source, driven by a small script/service that resolves G1's credential and starts the `runOnDemand` RTSP pull. | `deploy/systemd/`, new `deploy/mediamtx/` |
| G4 | Streaming Gateway proxy: validates G2's token, maps `cameraId` → MediaMTX's HLS path, reverse-proxies the `.m3u8`/`.ts` (or fMP4) requests. **Revised: embedded directly in `Federation.Api`** — `GET /api/v1/streams/{cameraId}/{*hlsPath}` (`StreamSessionEndpoints.ProxyAsync`) validates the bearer token, confirms it authorizes exactly the requested camera id, and reverse-proxies to MediaMTX via a named `HttpClient` (`StreamingOptions.MediaMtxBaseUrl`, default `http://127.0.0.1:8888`). No separate process — two processes to deploy (API + MediaMTX), not three. The original design (nginx `auth_request` + a standalone validation endpoint) is documented in §2's "why" note and kept available via `GET /api/v1/streams/validate` for a deployment that wants a dedicated proxy back, but it is not required. | `Federation.Api` (`StreamSessionEndpoints.cs`, `StreamingOptions.cs`) |
| G5 | Frontend: `hls.js` player in `VideoWallTileContent` (`frontend/src/features/videowall/VideoWallPage.tsx`), replacing the "Live feed not available" panel when a stream session can be established; falls back to the existing status-only tile if G2 returns an error (camera has no gateway configured, out of scope, etc.) — never a broken player. | `frontend/src/features/videowall/` |
| G6 | Scale/ops: MediaMTX metrics (it exposes Prometheus metrics natively) wired into whatever this project's eventual Federation.Worker/Api metrics story lands on (see `docs/MODEL-2-WORKER-HARDENING-PLAN.md`'s observability phase) — concurrent-stream count and per-stream bandwidth are the numbers that matter for capacity planning here, not camera count. | `deploy/`, monitoring |

---

## 4. Security notes worth stating explicitly

- The RTSP credential is resolved twice-removed from the browser: browser → signed token → proxy
  → MediaMTX → (once, on-demand) → camera. At no point does a credential cross a boundary the
  browser can observe.
- The signed stream token (G2) is deliberately **not** a general-purpose access token — it is
  scoped to one camera, short-lived, and does nothing else. A leaked stream URL only leaks one
  camera's view for a few minutes, not account access.
- `credential_access_log` already gives an audit trail for every credential resolution
  (`docs/AUTHORIZATION.md`); G1 rides that same mechanism, so "who watched which camera's raw
  credential get resolved, when" is already answered by existing infrastructure once G1 lands.
- MediaMTX itself should run under a least-privilege service account, network-segmented toward
  camera subnets only — the same posture `docs/ARCHITECTURE-MODEL-3.md` already requires of
  connector workers.
- The gateway's calls to G1 (and to the existing VMS resolve endpoint, if the same process ever
  needs it) run over mTLS between the calling service account and `Federation.Api`, per
  `ARCHITECTURE-MODEL-3.md` §9's existing requirement on this route class — inherited, not a new
  decision, and easy to silently drop on a bare-metal deployment where internal TLS feels
  optional. It is not optional for a route that discloses camera credentials.

---

## 5. Decisions made during review, and the one still open

Resolved (see §3/§4 above for the reasoning):

1. **G4's host, revised after initial implementation: embedded in `Federation.Api` itself**,
   proxying directly to MediaMTX — not a separate nginx process. Originally planned as nginx
   `auth_request` + a standalone validation endpoint (still available as `GET
   /api/v1/streams/validate` for a deployment that wants a dedicated proxy back); moved into the
   API to cut a deployment down to two processes instead of three. See §2's "why" note for the
   traffic-shape tradeoff this accepts.
2. **`STREAMING_GATEWAY` is its own machine role, holding only `camera.credential.resolve` (and
   whatever `camera.read`/`vms.read` scoping it needs to resolve a target) — never
   `DETECTION_WORKER`.** Concrete reason: `DETECTION_WORKER` also holds `observation.write`. The
   streaming gateway is browser/operator-facing — a materially larger attack surface than
   `ai-worker`'s backend-only VMS polling — so a compromise there must not also be able to forge
   ANPR/person/vehicle observations into the metadata layer that correlation, search, and
   alerting all trust. That's a data-integrity risk, not just a video-privacy one, reachable
   through a role the gateway has no functional need for.
3. **Token signing key management is the existing `JwtTokenService` signing-key ring**
   (`Auth:Jwt:SigningKeys`) — see G2 above. No second key-storage story to invent.

Still open:

4. **Whether G6's metrics land on a Prometheus endpoint added to `Federation.Worker`/`Api`, or
   stay MediaMTX-only for now** — tie this to whichever observability phase from
   `docs/MODEL-2-WORKER-HARDENING-PLAN.md` actually gets picked up. Whichever is chosen, G6 must
   track per-stream/per-reader **bandwidth**, not only concurrent-stream count — see §1.

---

## 6. Build order

1. **G1** (camera credential resolve endpoint) — foundational, everything else needs it. Not
   done until its mTLS transport requirement and its role decision (`STREAMING_GATEWAY`, not
   `DETECTION_WORKER` — §5) are both in, since both are cheap now and expensive to retrofit once
   the role/access-group is provisioned against a live deployment.
2. **G3** (MediaMTX deployed, manually configured for one test camera) — proves the RTSP→HLS
   path works at all, before any app-side auth is wired in. Can run in parallel with G1 since
   it doesn't depend on it for a manual smoke test.
3. **G2 + G4** (session token + authenticated proxy) — the real access-control surface; needs G1
   done so the proxy path can actually resolve a credential end-to-end.
4. **G5** (frontend player) — needs G2/G4 live to test against.
5. **G6** (metrics) — can land any time after G3, lowest priority.

---

## 7. End-to-end flow, as built

Two independent flows. Provisioning runs continuously in the background regardless of whether
anyone is watching anything; viewing only happens when an operator opens a tile, and is what
actually moves video bytes.

### 7.1 Provisioning (background, every ~60s — `PROVISION_INTERVAL_SECONDS`)

```
provision_paths.py  ──────────────────────────────────────────►  Federation.Api  ──►  Postgres
        │
        │ 1. GET /api/v1/cameras
        │    every camera the STREAMING_GATEWAY key's scope covers
        │
        │ 2. per camera, resolve a credential (list_cameras() → resolve_credential()):
        │      GET /api/v1/cameras/{id}/credential/resolve        (camera's own — tried first)
        │      404 → GET /api/v1/vms/{vmsId}/credential/resolve   (falls back to its VMS target)
        │      Both calls: X-Api-Key (STREAMING_GATEWAY), both write credential_access_log
        │      on the API side. Neither the resolved credential nor this script logs it.
        │
        └─ 3. POST http://127.0.0.1:9997/v3/config/paths/add|patch/cam-{cameraId}
               { "source": "rtsp://user:pass@camera-ip:554/...", "sourceOnDemand": true }
               (MediaMTX's own runtime config API, loopback only)
               Any MediaMTX path whose camera no longer appears in this run's list is deleted.
```

At the end of one cycle, MediaMTX knows **how** to reach every in-scope camera. It has not
connected to any of them — `sourceOnDemand: true` means it waits for a viewer.

### 7.2 Viewing (real-time, the moment an operator opens a video-wall tile)

```
Browser (LiveVideoTile)                Federation.Api                MediaMTX          Camera
        │                                │                               │                │
  1.    │──GET /api/v1/streams/          │                               │                │
        │  {cameraId}/session ──────────►│  checks camera.read + org/geo │                │
        │  (ordinary login token,        │  scope for THIS camera, THIS  │                │
        │   camera.read required)        │  user; mints a 5-min JWT:     │                │
        │◄─ { cameraId, token,           │  "only good for watching      │                │
        │     expiresAt } ───────────────│  this one camera"             │                │
        │                                │                               │                │
  2.    │──GET /api/v1/streams/          │                               │                │
        │  {cameraId}/index.m3u8 ───────►│  StreamSessionEndpoints       │                │
        │  Authorization: Bearer <token> │  .ProxyAsync validates the    │                │
        │  (hls.js request; browsers     │  token in-process — no DB     │                │
        │   can't attach a header to a   │  hit, just signature/expiry/  │                │
        │   bare <video>, so hls.js does │  audience — then checks the   │                │
        │   the fetching itself;         │  token's authorized camera id │                │
        │   repeated for every .ts       │  against the URL's: a token   │                │
        │   segment thereafter)          │  for camera A can't be        │                │
        │                                │  replayed against camera B    │                │
        │                                │  by editing the URL           │                │
        │                                │                               │                │
  3.    │                                │──GET /cam-{cameraId}/        │                │
        │                                │  index.m3u8 ─────────────────►│                │
        │                                │  (127.0.0.1:8888, MediaMTX,   │                │
        │                                │   same process, one HttpClient)                │
        │                                │                               │──RTSP pull
        │                                │                               │  starts HERE,
        │                                │                               │  first viewer
        │                                │                               │  only ───►
        │◄── HLS segments ───────────────│◄── HLS segments (remuxed H.264)◄──────────┘
        │                                │                               │                │
  4.    │  ~4 min in: LiveVideoTile      │                               │                │
        │  quietly repeats step 1 for    │                               │                │
        │  a fresh token before the      │                               │                │
        │  5-min one expires — hls.js's  │                               │                │
        │  xhrSetup reads the CURRENT    │                               │                │
        │  token via a ref, not the one  │                               │                │
        │  captured at mount             │                               │                │
        │                                │                               │                │
  5.    │  tile closed / camera swapped  │                               │                │
        │  → hls.js requests stop        │                               │                │
        │                                │        (a few sec later) sourceOnDemandCloseAfter:
        │                                │        30s elapses → MediaMTX closes the RTSP
        │                                │        connection to the camera
```

### 7.3 Where the credential is, at every point in time

```
DB (encrypted, at rest)
   → resolved ONCE per provisioning cycle by provision_paths.py
   → held ONLY in MediaMTX's in-memory runtime config (127.0.0.1:9997, unreachable
     from outside the host — mediamtx.yml binds api/hls/metrics to loopback only)
   → NEVER written to disk by provision_paths.py, never logged, never in mediamtx.yml itself
   → NEVER reaches the browser, or any HTTP response Federation.Api sends the browser

The browser only ever holds the 5-minute, single-camera, single-purpose session token from
step 1 above — cryptographically useless for anything except watching that one camera, for a
few minutes, through this one gateway.
```

### 7.4 What each hop actually checks, and why only one of them is "real" RBAC

| Hop | Check | Cost |
|---|---|---|
| Browser → `Federation.Api` (session mint) | `camera.read` + org/geo scope, for this caller, this camera | One DB-backed scope check — the real authorization decision |
| Browser → `Federation.Api` (every segment, `ProxyAsync`) | Token signature, expiry, audience (`trinetra-stream`), camera-id-in-token vs. camera-id-in-URL | Cryptographic only — no DB hit, because the scope decision already happened once, and the token is deliberately too short-lived and narrow to be worth re-checking against the DB on every one of dozens of segment requests per minute |
| Provisioner → `Federation.Api` (credential resolve) | `camera.credential.resolve` / `credential.resolve`, `STREAMING_GATEWAY` role only | Audited to `credential_access_log` every time, regardless of outcome |
| `Federation.Api` → MediaMTX | None — MediaMTX has no RBAC concept at all | This is exactly why the proxy step above exists and why MediaMTX binds loopback-only |
