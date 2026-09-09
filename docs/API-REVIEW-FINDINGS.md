# API Review Findings

Endpoint-by-endpoint review of the Federation API (`src/Trinetra.Federation.Api/Endpoints/*`),
started 2026-09-01, sweep completed 2026-09-04. Findings F1–F17, one per endpoint group plus
design-direction notes. **Nothing here is implemented yet** — this is the triage list.

Some findings were verified by a second pass (noted inline as "VERIFIED by dotnet-expert").
`CameraRepository` came out of the review as the reference implementation for scope enforcement
(both organization and geography dimensions, per-permission) — use it as the template when
fixing the scope findings elsewhere.

The **Priority Index & Implementation Sequence** below is the working order; the numbered
`Finding N` sections after it hold the evidence and fix notes.

═══════════════════════════════════════════════════════════════════════════════
## Priority Index & Implementation Sequence (built 2026-09-04)
═══════════════════════════════════════════════════════════════════════════════

### P0 · CRITICAL — do first, one PR each

1. **8-C1** API-key creation has NO escalation guard → any `apikey.manage` holder (incl. scoped
   STATE_ADMIN) mints a key in PLATFORM-ADMINS = full estate takeover. Chains to 13-H2
   (→ resolve every device credential). **Fix:** extract the 3-check chokepoint from
   `UserEndpoints.AddToGroupAsync` to a shared helper; run it in `ApiKeyEndpoints.CreateAsync`.
2. **15-H1** Arbitrary file write / path traversal — client-supplied `DetectionEventRequest.Id`
   used in the evidence file path. **Fix:** GUID-validate `Id`, server-generate filename, assert
   resolved path under root.

### P1 · HIGH — security / integrity

**GEOGRAPHY-SCOPE WAVE** (one design, many sites — `CameraRepository` is the correct template):

3. **9-H1** VMS write path checks org only; `GetAsync` geo check bypassed by the *org* unscoped
   flag. `ListAsync`/`OverviewAsync` have no geo clause. (invariant 12)
4. **13-H1** Credential write/resolve/status inherit 9-H1 via `ConnectorTargetRepository.GetAsync`
   — the "highest-privilege action" is reachable cross-geography. (fixed by #3)
5. **15-M1** Detection ingest + search enforce org only, no geography. (same fix pattern)
6. **14-M1** VERIFY `EventQueryRepository` ANDs both dimensions (likely same bug).
7. **16-L2/L4** VERIFY watchlist entry/alert/match queries.

**Other HIGH:**

8. **9-NEW-H** `PUT /vms/{id}` silently resets state to Active → re-arms a quarantined target
   (violates non-negotiable #4). **Fix:** preserve existing state in `ReplaceAsync`.
9. **8-H1** `ApiKeyRepository.RevokeAsync` not authority-scoped → cross-dept DoS (kill any key). ✅ PR5
10. **4-C1** No token revocation — deactivation / group removal / password reset ineffective
    ≤8h; `ChangePasswordAsync` doesn't re-check status/lockout & clears `locked_until`. **Fix:**
    `token_version` claim checked in `OnTokenValidated`, bumped on the relevant events;
    `POST /auth/logout`.
11. **5-H1** Scoped `*.manage` callers 500 on every hierarchy create/deactivate (reach check
    runs outside the txn). **Fix:** pass `work.Transaction`; integration test with a scoped caller. ✅ PR6
12. **5-H2** TOCTOU on hierarchy deactivate/reparent (no row lock / version guard). ✅ PR6
13. **6-H1** Unscoped reads: users / groups / group-members list every account & the SUPER_ADMIN
    roster to any scoped `user.read`/`group.read` holder. (invariant 11) ✅ PR5
14. **8-H2** `api-keys` list unscoped → privilege map of every service account. ✅ PR5
15. **9-H2** VMS hard-delete: no non-Active guard; silent `camera_status_history` orphan.
16. **9-H3** VMS delete gated `vms.update` not a dedicated `vms.delete`.
17. **15-H2** Detection base64: no try/catch (500) + no size / body cap (DoS / disk-fill). ✅ done 2026-09-08 (uncommitted)
18. **4-H2** No MFA anywhere (incl. bootstrap admin) — likely compliance blocker.
19. **4-H3** Auth events (login ok/fail, lockout, token issue, key use) bypass the audit trail. → PR7b
20. **4-H4** Login is a timing oracle (unknown user skips PBKDF2) — defeats the stated
    anti-enumeration. **Fix:** always verify against a dummy hash. ✅ PR7a
21. **4-H1** JWT signing: single symmetric key, no `kid`, no key ring, `ValidAlgorithms` unpinned.
    ✅ algorithm pinned (PR7a); key ring still open → PR7d
22. **4-H5** API-key AUTH path: no rate limit + DB round trip pre-auth (brute-force +
    amplification). Related **8-NEW-H**: `TouchAsync` write + 3rd connection on every M2M request.
23. **8-H3** API-key creation doesn't reject a DRAFT/DISABLED group → latent grant on activation. ✅ done (`GroupGrantGuard` rejects non-ACTIVE with 409, since `2504dde`; regression test + doc added 2026-09-08)

### P2 · MEDIUM — correctness / robustness

**AUDIT-FIDELITY WAVE** ✅ done 2026-09-08 (uncommitted), +6 tests (`WatchlistRepositoryTests` ×4,
`VmsLifecycleTests.SetState_ReturnsThePriorStateAndOrgUnit`, sabotage-checked):
`5-M6, 5-M5b, 6-M7, 9-L1, 9-NEW-M, 10-L4, 13-M1, 16-M2, 16-M3` — audit rows that pass
`before:null` / `after:<request DTO>` / `organizationUnitId:null`, or (16-M2) record nothing at
all.
- **16-M2** — `WatchlistRepository.DeactivateAsync` now returns the entry; the audit `before`
  carries the plate/reason/severity and the org dimension = the entry's unit.
- **16-M3** — `AcknowledgeAlertAsync` returns `{OrganizationUnitId, AlreadyAcknowledged}`; the
  endpoint records the transition (before/after ack) with the alert's org, and a no-op re-ack
  is now a **204** (no audit row), not a 404.
- **9-L1** — `ConnectorTargetRepository.SetStateAsync` returns `TargetStateChange{PriorState,
  OrganizationUnitId}` via a CTE; audit records both (was `before:null` + `organizationUnitId:null`).
- **9-NEW-M** — VMS `RegisterAsync` audits the persisted target (`Redact(target!)`) and keys the
  org dimension off it, not the raw request DTO.
- **10-L4** — `ReconcileResult` gained `OrganizationUnitId` (from the `cameras` UPDATE RETURNING);
  reconcile audit now carries it (was null).
- **5-M5b** — Hierarchy create org/unit/area audits `ToResponse(entity with {Id})` — generated
  id + the status the server applied — not `after: request`.
- **6-M7** — `AccessGroupEndpoints` create-group + AddScope audit the persisted shape (id +
  effective status / normalised scope type); ORGANIZATION scope adds carry the scoped unit.
- **5-M6** — deactivate audit `after` now includes `childStrategy` + `newParentId`, and the org
  dimension is keyed on the unit for `organization_unit` deactivations. **PARTIAL:** the list
  of cascaded / reparented descendant ids is still not recorded — needs `DeactivateUnitAsync` /
  `DeactivateAreaAsync` to return that set on success (follow-up).
- **13-M1** — VERIFIED: `SecretWriter.WriteAsync` already wrote to the main `config_audit`
  trail; switched it from a hand-rolled INSERT to `work.AuditAsync` so its JSON shape matches
  every other audit row.
- Adjacent: **`RoleEndpoints.UpdateAsync` stale-read bug** (v1.12) — line ~164
  `repo.GetAsync(id, caller, ct)` ran on a NEW pooled connection after the in-transaction
  `UpdateAsync`, returning PRE-update data (wrong response body + weak audit `after: request`).
  ✅ FIXED 2026-09-08: `RoleWriteResult` gained `Detail` — `RoleRepository.Create/UpdateAsync`
  now reload via `LoadAsync(c, work.Transaction, id, ct)` (in-transaction) and return it; the
  endpoint uses `result.Detail` for both the response and the audit `after` (create audit also
  moved off `after: request`). +1 assertion in `RoleCrudTests`, sabotage-checked.

**PAGINATION WAVE** ✅ done 2026-09-09 (uncommitted), PR9. `5-M7`, `6-M6`, `8-M5`, `9-M3`,
`10-M2`, `15-M2`, `16-M4`, `17-L*(list)`.
- **Design (owner):** pagination is **opt-in**. `?page` (1-based) + optional `?pageSize`
  (default 50, clamped) → `{ items, page, pageSize, total, totalPages }` envelope. Omit `page`
  → the bare array as before, **hard-capped** (1000, or 2000 for VMS cameras / 5000 for the GIS
  feed); when the cap trims, `X-Result-Capped: true`. `X-Total-Count` always set. Offset
  pagination, `count(*) OVER()` for the total in one round trip.
- Shared: `Contracts/Pagination.cs` (`PageQuery`, `PageResult<T>`, `Paginate.Render`),
  `Repositories/Pagination.cs` (`PagedRows<T>`, `PageWindow`).
- Endpoints: `GET /users`, `/access-groups`, `/access-groups/{id}/members`, `/organizations`,
  `/organizations/{id}/units`, `/geographic-areas`, `/geographic-areas/{id}/children`,
  `/api-keys`, `/vms/{id}/cameras`, `/worker-health`, `/watchlist/alerts`. New paged repo
  methods (`ListPageAsync` / paged overloads) alongside the unbounded CLI/test ones.
- **`16-M4`** also adds `acknowledged` (bool) + `plate` filters to `/watchlist/alerts`.
- **`15-M2`** detection search: no offset paging (partitioned time-series — same reason
  `EventEndpoints` uses keyset). Fixed the actual DoS: **max 31-day window (400 otherwise)** +
  `limit` hard-capped at 500.
- **`10-M2`** GIS feed: body stays a `FeatureCollection` (can't wrap GeoJSON); pagination via
  `page`/`pageSize` + `X-Total-Count` / `X-Result-Capped` / `X-Page` / `X-Page-Size` headers.
- Tests: `PaginationTests` (9 unit), `UnscopedReadScopeTests.Users_ListPage_*` (2, offset
  sabotage-checked). Suite: 116 unit / 236 integration + 18 pre-existing.
- NOT covered (own findings, not this wave): `17-M1/M2/M3` heartbeat spoofing, `17-L3` worker
  list is still unscoped (hostname disclosure).

**VALIDATION / ERROR-SHAPE WAVE:**
`5-M2` area_type free text, `5-M9` over-long code → 500, `5-M10` phantom 204, `6-M2/M3` scope-field
validation, `9-M1` VerifyTls non-nullable (TLS downgrade), `9-M2` org/site ACTIVE + consistency,
`9-M4` CapabilitiesAsync 404 leak, `10-M3` NullableString silent truncation, `16-M1` dup plate →
500 not 409, `9-NEW-L` file:// endpoint, `9-NEW-L` malformed notes JSON → 500.

**ESCALATION / SCOPE (non-P1):**
`6-M1` geography asymmetry in group scope add/remove, `5-M1` org read perm mismatch
(`geography.read` vs `organization.read`) ✅ PR6.

**OTHER MEDIUM:**
`4-M1` must-change-password advisory only, `4-M3` login rate-limit per-IP only, `4-M5` email
unvalidated/non-unique (blocks F2/F3), `4-M6` no breached-password screen, `4-M7` CORS, `4-M8`
bootstrap password lingers, `6-M4` lockout-guard TOCTOU, `6-M5` email, `8-M1` no key max
lifetime, `8-M2` no key rotation endpoint, `10-M4` bulk import 500 sync txns, `10-M5` bulk audit,
`10-M6` cursor code-reuse anomaly, `10-M7` reconcile location consistency, `10-M8` GIS feed
per-prop JsonElement alloc, `14-M2` `event.acknowledge` seeded w/ no endpoint, `15-M3`
ResolveCamera pre-scope, `17-M1` heartbeat spoofable, `17-M2` client-supplied lastHeartbeatAt,
`17-M3` read/write share one perm + STATE_ADMIN holds worker.heartbeat.

### P3 · LOW — polish / hygiene

`F1` (strip "Model N" from OpenAPI), `5-L4/L5/L6`, `6-L1..L6`, `8-M3/M4/NEW-L`, `9-L2/L3/L4`,
`10-L1/L2/L3/L5/L6/L7`, `13-L1/L2`, `14-L1/L2/L3`, `15-L1..L4`, `16-L1/L3`, `17-L1..L4`,
`4-L1..L6`, `4-M2` (refresh tokens — or fold into 4-C1).

### Scope additions (agreed)

- Guarded DELETE for sites/units/areas/orgs (`5-L3`) — reference-checked, 409 + blocker list.
- **F2** self-service forgot-password flow.
- **F3** SSO (OIDC/SAML) seam — shape the user/token model now.

### Design track — decide before building (not bugs)

- **F7** Custom composed roles + maker-checker APPROVAL workflow. Supersedes 6-H2 (missing
  group-activate route) and 6-L4 (roles read-only). Build ONE `approval_request` model for
  {role create, group create, sensitive membership}. Blocks a lot of F6.
- **F11** Registry/GIS is NVR-unaware — decide if per-channel manual onboarding is acceptable for
  phase 1 or an NVR needs bulk-adopt + `targetId` filter + federated→registry health.
- **F12** API-key strength & GET non-disclosure — VERIFIED OK, no action (kept for the record).

**Suggested build order:** P0 (#1, #2) → geography-scope wave (#3–7) → #8–12 → 4-C1 + auth audit
(#10, #19) → F7 design decision → audit-fidelity + pagination + validation waves → P3 sweep.
F2/F3/DELETE fold in wherever the touched files are already open.

### PR plan (agreed 2026-09-04)

The API is **not deployed anywhere yet**, so the "a version file is never edited once applied"
rule does not bite — still prefer new `v1.7+` files over editing `v1.sql`. Branch per PR off
`main`.

| PR | Contents | Schema |
|----|----------|--------|
| PR1  | **P0**: 8-C1 + 15-H1 — ✅ done, commit `d86c6ee` | — |
| PR2  | **Geography-scope wave**: 9-H1, 13-H1, 15-M1, 14-M1 — ✅ implemented + BA/dotnet-expert reviewed, 13 integration tests added (not committed); 16-L2 verified n/a | — |
| PR3  | **VMS lifecycle**: 9-NEW-H, 9-H2, 9-H3, 9-M1, 9-M2 — ✅ done, BA/dotnet-expert plan reviewed, 12 new tests | `vms.delete` (v1.7.sql) |
| PR4  | **Token revocation + refresh tokens** (scope expanded): 4-C1 + 4-M2 + `POST /auth/logout` + `POST /auth/refresh` + short access token + `ChangePasswordAsync` status re-check — ✅ done, BA + dotnet-expert + postgres-expert reviewed, fix pass applied, 15 new tests | `token_version` + `refresh_token` table (v1.8.sql) |
| PR5  | **Unscoped reads**: 6-H1, 8-H1, 8-H2 — ✅ done, BA + dotnet-expert plan reviewed, 17 new tests | — (code-only) |
| PR6  | **Hierarchy correctness**: 5-H1, 5-H2, 5-M1, 5-M8 (folded in) — ✅ done, BA + dotnet-expert plan + implementation reviewed, 19 new tests | — (code-only) |
| PR7  | **Auth hardening** — split into 5 sub-PRs (BA + dotnet-expert, 2026-09-07): | |
| PR7a | 4-H4 (login timing oracle) + 4-L3 (locked-account counter DoS) + pin JWT `ValidAlgorithms` — ✅ done, both agents' plans converged, dotnet-expert impl-reviewed, +10 tests (4 unit + 6 integration) | — (code-only) |
| PR7b | 4-H3 auth event audit trail — ✅ done, spec'd by dotnet-expert, +15 tests | `auth_audit` table + `authaudit.read` (v1.9) |
| PR7c | 4-H5 API-key auth rate limit + `last_used_at` coarsening + 8-NEW-H — ✅ done (residual: per-IP unknown-key lookup ceiling is a follow-up), +10 tests | — (config; `IMemoryCache`) |
| PR7d | 4-H1 JWT signing **HMAC key ring** (owner's call) — ✅ done, +13 unit tests | config shape (`SigningKeys` list) |
| PR7e | password history + minimum age (4-M5); breached-password screening (4-M6) DEFERRED to its own PR — ✅ done, +11 tests | `password_history` table (v1.10) |
| —    | 4-H2 **MFA** → own design track, ~10 policy decisions; not a fix-PR | schema TBD |
| —    | 4-H6 API-key entropy — **refuted**, keys already server-generated 256-bit CSPRNG | — |
| PR8  | **Audit-fidelity wave** (P2) | — |
| PR9  | **Pagination wave** (P2) | — |
| PR10 | **Validation / error-shape wave** (P2) | — |
| PR11 | **P3 sweep** incl. F1 | — |
| F7   | separate feature branch, **after** the design decision | approval tables |

Status: **not started.**

═══════════════════════════════════════════════════════════════════════════════

## Finding 1 — internal "Model N" jargon leaks into the public OpenAPI reference

Consumer-facing OpenAPI text uses design-doc vocabulary ("Model 3 (VMS Federation)", "Model 2's
AI worker", "Model 1's registry"). Rephrase by function. Locations + proposed wording:

- `OpenApi/TagDescriptionTransformer.cs:28` — doc description "Model 3 (VMS Federation)" → "the Trinetra VMS federation platform"
- `OpenApi/ApiTags.cs:118` — Detections tag "Model 2's vehicle/plate/OCR detections" → drop "Model 2's"
- `Endpoints/VmsEndpoints.cs:148` — "Model 3 never touches video / to Model 2" → "This platform / a video-analytics consumer"
- `Endpoints/HierarchyEndpoints.cs:177` — "belongs to Model 1" → "belongs to the camera registry"
- `Endpoints/CredentialEndpoints.cs:80` — "(`ai-worker/`, Model 2)" → "(`ai-worker/`)"
- `Endpoints/DetectionEndpoints.cs:14,18,36` — drop "Model 2's" / "Model 3's boundary applies to Model 2"
- `Endpoints/WorkerHealthEndpoints.cs:10` — "Model 2's AI-worker processes" → "AI-worker processes"
- `Contracts/Responses.cs:104`, `Contracts/Contracts.cs:143` — "Model 1's registry identifier", "Model 2's AI worker" → drop model refs

Leave internal XML docs in `Federation.Core` / `Federation.Adapters` as-is.

## Finding 2 — self-service "forgot password" flow missing

`AuthEndpoints` has self-service change (old-password check already present, `AuthEndpoints.cs:192`)
and admin reset (`POST /users/{id}/password`) already exists. Missing: unauthenticated
forgot-password (request → emailed/one-time token → set new password without old one).
Need: token table w/ short TTL + single use, rate limiting, same-response-for-unknown-user,
audit row, no email enumeration. Decide delivery channel (email service exists? probably not yet).

## Finding 3 — no SSO support; build the seam now

Auth is password + API key only. `JwtTokenService.Issue` called only after local credential check
in `TryAuthenticateAsync`. For later SSO (OIDC/SAML): external IdP callback that issues our JWT
from a verified assertion, JIT user provisioning + account linking, mark account SSO-only
(no local password / disable `/auth/password` + `/auth/login` for it), map IdP claims →
access groups / scope. Want the user + token model shaped so this drops in later.

## Finding 4 — authentication gaps (second-pass review, 2026-09-01)

Deferred; triage before implementing. Severity-ordered.

**PR7 SPLIT (2026-09-07, BA + dotnet-expert planned independently, converged):** PR7 is 5 sub-PRs
— 7a (login hardening), 7b (auth audit), 7c (API-key auth path), 7d (JWT key ring), 7e (password
history). MFA (4-H2) is its own design track (~10 policy decisions). 4-H6 is refuted (keys are
server-generated 256-bit CSPRNG — Finding 8 second pass). See the PR PLAN table above.

**PR7a RESOLUTION (2026-09-07) — 4-H4, 4-L3, 4-H1 (partial):**
- **4-H4** — `PasswordHasher.Decoy` (computed once from a random secret, carries
  `DefaultIterations` so it tracks the cost as it rises). `AuthEndpoints.TryAuthenticateAsync`
  runs `PasswordHasher.Verify(password, user?.Password ?? Decoy)` **unconditionally** before the
  account-state checks — a missing / inactive / locked account now costs the same full PBKDF2
  grind as a wrong password, so response time no longer reveals which usernames exist. `LoginAsync`
  made `internal` for the timing test.
- **4-L3** — `UserRepository.RecordFailedLoginAsync` gained `AND (locked_until IS NULL OR
  locked_until <= now())` — an attacker's attempts during a lockout window no longer push
  `locked_until` further out (indefinite-lock DoS).
- **4-H1 (partial)** — `JwtTokenService.ValidationParameters.ValidAlgorithms = [HS256]` pinned,
  closing `alg:none` / downgrade ahead of the full key ring in PR7d.
- Tests: `PasswordHasherTests` + `JwtTokenServiceTests` (unit, 4) + `LoginHardeningTests`
  (integration, 6 — unknown / locked / wrong-pw each spend ≥ half of one measured PBKDF2 grind
  (relative floor, not a hardcoded ms), correct pw still succeeds, RecordFailedLogin frozen while
  locked). Sabotage-verified ×3 (short-circuit Verify: 2 fail; drop the locked-until guard: 1
  fails; drop ValidAlgorithms: unit test fails).
- Residual (accepted, per dotnet-expert review): a *known* bad attempt still does an extra
  `RecordFailedLoginAsync` UPDATE (~1–5ms) that an *unknown*-user attempt skips — sub-hash-noise
  now that PBKDF2 dominates the response, not worth branching for.
- Build 0 warnings. 73 unit + 146 integration pass (+10). Same 18 pre-existing `vendor_kind` fails.
- NOT in 7a (both agents kept it minimal): 4-M1 (must-change-password middleware — needs the
  restricted-token filter that 7e/MFA also want), 4-M3 (per-username rate partition + forwarded
  headers). `ChangePasswordAsync` timing left as-is (authenticated, not an enumeration oracle).
Status: PR7a is NOT yet committed — it ships in one batch with 7b–7e (HEAD = PR6, 69df7c1).

**PR7b–7e RESOLUTION (2026-09-07) — specs by dotnet-expert (2 parallel), implemented together
for one commit at the owner's request:**

- **7b (4-H3) auth event audit trail.** New `db/versions/v1.9.sql`: `auth_audit` — monthly
  RANGE-partitioned like `config_audit`, append-only, `outcome` CHECK (success|failure|lockout|
  revoked), `user_id`/`api_key_id` both nullable `ON DELETE SET NULL`, `presented_username`
  (≤256, only when `user_id` is null), `source_address`/`user_agent`/`jti`/`detail`. Wired into
  `ensure_audit_partitions()` (one array element), new `drop_auth_audit_partitions_before(cutoff)`
  keyed only on `auth_audit` (its OWN retention period), `retention_status` view gains an
  `auth_audit` arm. New permission `authaudit.read` → **SUPER_ADMIN only**, NOT bundled with
  `config.audit.read`. `db/objects/` mirrors added/edited.
  `AuthAuditRepository` (Storage): `WriteAsync(UnitOfWork,…)` for events that ride a mutation,
  `WriteBestEffortAsync(…)` for pure events (own pooled connection, swallows all but cancellation
  — an audit outage must not become an auth outage), `ListAsync(query)` keyset on
  `(occurred_at DESC, id DESC)`. `AuthAuditEvents` constants, `RequestClientMeta` (raw
  `RemoteIpAddress` — forwarded-header hardening is 4-M3, out of scope). `AuthEndpoints`
  (login/refresh/logout/password) + `UserEndpoints.ResetPasswordAsync` + `ApiKeyAuthenticationHandler`
  write rows; `login.success` / `token.refresh` / `password.*` ride the existing UoW,
  failures/lockout/api-key are best-effort. `RecordFailedLoginAsync` → `Task<bool>` (true only on
  the lockout transition — the `WHERE` already excludes a locked row). New `GET /api/v1/auth-audit`
  (`authaudit.read`, paged, newest first, append-only — no write route). `RetentionOptions.AuthAuditMonths`
  (default 24, one-month floor) + `RetentionCutoffs.AuthAudit` + `MaintenanceService` wiring.
- **7c (4-H5 + 8-NEW-H) API-key auth path.** `services.AddMemoryCache()`. New `ApiKeyAuthSupport`:
  `ApiKeyFormat.IsWellFormed` (64 lowercase hex, allocation-free), `ApiKeyRateLimiter` (per-node
  fixed window, 20/min, keyed `sha256(key)[..16] + IP` — key-hash primary so a junk header on a
  shared IP can't lock out a real caller), `ApiKeyGrantCache` (per-node, 45s TTL). Handler
  rewritten: rate-limit → 429 (with a `HandleChallengeAsync` guard so the framework 401 doesn't
  overwrite it) BEFORE any DB work; format check; then a grant-cache lookup that on a miss does
  `FindAsync` + `GrantsAsync`, caches `{Key,Grants}` + an id→hash index, and calls the now-coarse
  `TouchAsync`. `ApiKeyRepository.TouchAsync` → `Task<bool>`, `UPDATE … WHERE last_used_at IS NULL
  OR last_used_at < now() - 5min RETURNING TRUE`. `ApiKeyEndpoints` revoke busts this node's cache
  entry. **Weakens** the "API keys re-resolve grants per request" guarantee to ≤45s fleet-wide —
  documented in AUTHORIZATION.md.
- **7d (4-H1) HMAC signing-key ring** (owner chose HMAC over RS256). `JwtOptions.SigningKey`
  (scalar) → `SigningKeys` (`List<JwtSigningKey{Kid,Value}>`), first entry signs, all validate;
  scalar kept as a back-compat alias → one-element ring, kid `"legacy"` (env `Auth__Jwt__SigningKey`
  still works). `JwtTokenService` ctor validates each key (≥32 bytes, valid base64, non-empty
  distinct Kid) or throws the same helpful startup error. `Issue` stamps the primary `kid`
  automatically (SigningCredentials.Key.KeyId). `ValidationParameters.IssuerSigningKeys` = the
  ring; `IssuerSigningKey` (singular) dropped; `ValidAlgorithms` pin kept. Rotation is
  restart-based (add non-first → restart fleet → promote → drop old a cycle later). No JWKS, no
  hot reload, no DB table, no RS256 half-support.
- **7e (4-M5) password history + minimum age.** New `db/versions/v1.10.sql`: `password_history`
  (user_id CASCADE, the four `password_*` columns, `set_at`). `PasswordPolicy` (const
  `HistoryDepth = 5`, `static readonly MinimumAge = 24h` — policy not config). `UserRepository`:
  `SetPasswordAsync` now snapshots the outgoing hash into history + prunes to the newest N, all in
  the UoW; new `RehashPasswordAsync` (login cost-upgrade — same password, no history row) — the
  login rehash path switched to it; new `LoadPasswordHistoryAsync` + `PasswordHistoryEntry`. New
  `PasswordChangeGuard.Check(newPassword, history, enforceMinimumAge)` → 400 on reuse or (self
  only) too-recent. Wired into `AuthEndpoints.ChangePasswordAsync` (`enforceMinimumAge:
  !user.MustChangePassword` — a forced change after an admin reset must not be age-blocked by the
  reset it is completing) and `UserEndpoints.ResetPasswordAsync` (false — an admin unlocking a stuck user isn't age-blocked,
  but reuse still applies). Breached-password screening (4-M6) deferred to its own later PR.
- Tests: `AuthAuditTests` (11), `ApiKeyCoarseTouchTests` (4), `ApiKeyAuthSupportTests` (unit, 6),
  `RetentionOptionsTests` (unit, 3), `JwtTokenServiceTests` extended to 13 (ring: issue-under-A /
  promote-B / old-token-valid / kid-not-in-ring-rejected / ctor validation), `PasswordHistoryTests`
  (11). Sabotage-verified: 7b lockout-audit RETURNING; 7c coarse-touch WHERE; 7d kid stamp.
  `InternalsVisibleTo("Trinetra.UnitTests")` added for the pure API-key helpers.
- **dotnet-expert consolidated review: SHIP-WITH-FIXES — applied:**
  (1) 7c — `ApiKeyRateLimiter` now returns `Allowed / JustLimited / AlreadyLimited`; the
  `apikey.ratelimited` audit row is written **only on the window transition**, so a sustained
  junk flood no longer produces unbounded INSERTs (it was partly reintroducing the 4-H5
  amplification). `GetOrCreate` null (create/evict race) now falls through to Allowed, not a 500.
  (2) 7e — `SetPasswordAsync`'s history-snapshot SELECT gained `FOR UPDATE` so two concurrent
  changes for one user serialize on the row before either snapshots (else a genuinely-used
  intermediate password could be absent from history); prune `ORDER BY set_at DESC, id DESC` for
  a deterministic tie-break. (3) doc: `OPERATIONS.md` now states the partition-maintenance pass
  is not optional (else `*_default` grows unbounded). (4) 7a xmldoc notes `failed_login_count`
  is not reset on lockout expiry (noisy re-lock, not a defect). Not done (agent's own "not
  blocking"): making the 45s grant-cache TTL configurable — kept as a const, flagged for a
  follow-up if incident response needs to drop it without a redeploy.
- **BA consolidated review: was BLOCK on 7e, now resolved — applied:**
  (a) **[7e BLOCK]** `ChangePasswordAsync` skips the 24h min-age check when `MustChangePassword`
  is true — the forced follow-up after an admin reset was 400-ing the user into a dead end.
  Test `ChangePassword_ForcedChange_NotAgeBlocked` + `_StillRejectsAReusedPassword`, sabotage-
  verified. (b) **[7b wording]** "the stored row never says whether the username was real" was
  false and self-contradictory — reworded in v1.9.sql / AUTHORIZATION.md §1 / the endpoint
  description: the *response* is coarse, the *trail* records `user_id`-null vs `presented_username`
  for an investigator (SUPER_ADMIN only). (c) **[7b test]** `AuthAuditRead_IsSuperAdminOnly_
  NotBundledWithAuditRead` — `authaudit.read` is granted to SUPER_ADMIN and no other role;
  STATE_ADMIN holds the general `audit.read` but not this one. (d) **[7c tests]** new
  `ApiKeyAuthHandlerTests` — 429 returned before any DB work (one ratelimited row per window,
  not per request), malformed key rejected, grant cache serves the second call, revoke-style
  bust clears both cache entries. (e) **[7c/4-H5 downgrade]** the finding is "amplification
  reduced, not closed" — the key-hash-primary limiter lets a flood of *well-formed distinct
  unknown* keys from one IP still reach `FindAsync` once per key; a per-IP lookup ceiling is a
  logged follow-up. (f) doc: rotation runbook now notes refresh-token sessions survive rotation
  and to confirm every host restarted before promoting; §1 45s note covers group status changes.
- **Logged follow-ups (own findings, not this batch):** per-IP unknown-key lookup ceiling
  (residual 4-H5); API-key success audit keyed on `(key, source_address)` so a new IP always
  records; `PasswordPolicyOptions` with stricter-only floors (accreditation may mandate specific
  numbers); config kill-switch for the grant-cache TTL (`0` = re-resolve per request);
  `auth_audit_default` (like `config_audit_default`) is never retention-pruned.
- Build 0 warnings. 100 unit + 179 integration pass (+47 across 7b–7e + review fixes). Same 18
  pre-existing `vendor_kind` fails.
- Out of scope, noted: forwarded-headers / trusted-proxy (4-M3); per-request API-key-use audit
  (coarse only — sampled on the 5-min touch); 4-M1 middleware; 4-M6 breached-password list.
Status: 7a–7e implemented + BA/dotnet-expert plan AND consolidated review + fixes + tested +
sabotage-checked. Ready for ONE commit (7a + 7b + 7c + 7d + 7e together). NOT YET COMMITTED
(HEAD = 69df7c1 = PR6).

**PR4 RESOLUTION (2026-09-06):** 4-C1 fixed, and 4-M2 (refresh tokens) folded in when Jaydip
expanded the scope.
- `platform_users.token_version` (INTEGER, monotonic) — new `trinetra:tokenver` claim, checked
  every request in `ConfigureJwtBearer`'s `OnTokenValidated` against the live user row (which
  also fails a non-ACTIVE user). Bumped + all refresh tokens revoked, in one transaction, on:
  self password change, admin reset, deactivation (ACTIVE→non-ACTIVE only), group REMOVAL (not
  add — a grant propagates on next refresh), `POST /auth/logout`, and refresh-token replay.
- Access token shortened to ~15 min (`JwtOptions.AccessLifetime`). New `refresh_token` table
  (hash only, 8h sliding, `used_at`/`replaced_by`/`revoked_at`). New `POST /auth/refresh`
  rotates the pair, re-resolves permissions fresh from the DB, treats replay of a rotated token
  outside a 10s grace window as theft (revoke everything). New `POST /auth/logout`.
- `ChangePasswordAsync` now re-checks Status/lockout before verifying the current password —
  closes the self-reversible-deactivation hole in the same request.
- Login/refresh response shape changed to `{ accessToken, accessExpiresIn, refreshToken,
  refreshExpiresIn, mustChangePassword }`. `db/versions/v1.8.sql`. API keys untouched.
- **Accepted test gap** (same posture as PR3's 9-H2): the end-to-end 401 from `OnTokenValidated`
  needs a `WebApplicationFactory` the suite doesn't have. Everything it depends on
  (`GetTokenVersionAsync`, every bump trigger, all `/auth/refresh` + `/auth/logout` +
  `/auth/password` branches) is covered by 14 real-Postgres tests in `TokenRevocationTests.cs`.
- **Deferred to follow-ups**: per-device session list (4-M4), refresh-token cleanup sweep,
  absolute session-age cap, MFA (4-H2), JWT key ring / RS256 (4-H1).

**PR4 fix pass (2026-09-06, BA + dotnet-expert + postgres-expert review):**
- **dotnet-expert bug #1 (blocking):** `ChangePasswordAsync` bumped `token_version` on the
  uncommitted `UnitOfWork`, then `IssuePairAsync` re-read it on a pooled connection and minted a
  token against the *stale* value — 401 on first use. Fixed: `BumpTokenVersionAsync` now
  `RETURNING token_version`; `SessionRevocation.EndAllAsync` returns the new value;
  `IssuePairAsync` takes `int? knownTokenVersion`. New regression test decodes the post-change
  access token and asserts `tokenver` == stored value (sabotage-verified).
- **Clock skew (#2/#8):** `RefreshTokenRepository.FindAsync` now computes `is_expired` /
  `is_replay_past_grace` in SQL against `now()`, not in C# — one clock near the 8h boundary.
- **Header leak (#3):** `ConfigureJwtBearer` sets `IncludeErrorDetails = false` so a
  `context.Fail` reason never reaches `WWW-Authenticate: error_description`.
- **postgres-expert:** dropped the `refresh_token.replaced_by` self-FK (plain UUID forensic
  chain — lets the nightly expiry sweep delete oldest-first without FK side-effects);
  `token_version` moved to end of `platform_users` mirror to match pg_dump order.
- New `SessionRevocation.EndAllAsync` helper centralises bump-then-revoke lock order across all
  6 call sites (logout, self password change, admin reset, deactivation, group removal, replay).
- 15 tests in `TokenRevocationTests.cs`, all green. Pre-existing 18 stale-`vendor_kind` failures
  unrelated.

### CRITICAL

- **C1** — ✅ FIXED by PR4 (above). Token revocation: HS256 tokens were stateless, `jti` unused.
  Deactivation, group removal, admin password reset all ineffective up to 8h. `ChangePasswordAsync`
  also didn't re-check Status/lockout and `SetPasswordAsync` clears `locked_until` → deactivation
  was self-reversible.

### HIGH

- **H1** JWT signing key: single symmetric HS256, no `kid`, no key ring, `ValidAlgorithms`
  unpinned. Rotation = global outage; secret on every node. Move to RS256/ES256 + kid +
  IssuerSigningKeys ring; at minimum current+previous 2-key validation. Pin ValidAlgorithms.
  → **`ValidAlgorithms` pinned in PR7a; HMAC key ring done in PR7d** (owner chose HMAC over
  RS256 — SSO/F3 swaps to the IdP's JWKS regardless). ✅
- **H2** No MFA/2FA anywhere incl. bootstrap admin. At least TOTP for accounts with user.manage
  or an org-unscoped group. Likely compliance requirement.
- **H3** — ✅ FIXED by PR7b. Auth events (login ok/fail, lockout, refresh, replay, logout,
  password change/reset, rehash, API-key auth) now write to the new append-only `auth_audit`
  (v1.9) with IP / UA / jti; `GET /api/v1/auth-audit` gated `authaudit.read` (SUPER_ADMIN only).
  Login failures coarse; API-key success sampled.
- **H4** — ✅ FIXED by PR7a. Login timing oracle: `TryAuthenticateAsync` short-circuited `||` so
  an unknown user returned ~0ms vs 600k PBKDF2 for a known one. Now runs `Verify` against
  `PasswordHasher.Decoy` unconditionally.
- **H5** — ✅ mostly fixed by PR7c; one residual is a follow-up. Per-node fixed-window limiter
  (20/min, keyed **key-hash + IP**) rejects with 429 before any DB work; 64-hex format pre-check;
  a 45s per-node grant cache removes the double round trip; `TouchAsync` is now coarse (8-NEW-H,
  ≤ 1 write/key/5min). **Residual (follow-up):** the limiter is key-hash-primary by design, so a
  flood of *well-formed but distinct unknown* keys from one IP is a fresh 20/min partition each
  and still reaches `FindAsync` once per key — amplification is *reduced* (no `GrantsAsync`, no
  `Touch`), not closed. Needs a per-IP lookup ceiling or a short negative cache. Logged.
- **H6** API key entropy not enforced server-side — **REFUTED** in Finding 8 (the endpoint
  generates a 256-bit CSPRNG key server-side). Kept for cross-reference only.

### MEDIUM

- **M1** `must_change_password` advisory only — no middleware blocks a pwchange-flagged token
  from other endpoints. Add filter rejecting all but /auth/password (+/auth/logout) when claim
  present.
- **M2** No refresh tokens / idle timeout — fixed 8h, no sliding. Refresh mechanism also gives
  substrate for C1 + M4.
- **M3** Login rate limiting per-IP only; add per-username partition. Verify NetworkOptions /
  forwarded-headers config for prod (proxy IP collapse, or spoofable XFF).
- **M4** No session management / concurrent-login control / "sign out other devices".
- **M5** — ✅ FIXED by PR7e. `password_history` (v1.10) — last 5 blocked on both change and
  reset; a self-service change is refused while the current password is < 24h old (admin reset
  exempt from the age check).
- **M6** No breached/common-password screening. Deferred by the owner to its own PR (it needs a
  bundled offline wordlist — a data-management decision worth isolating). PR7e ships history +
  min-age without it.
- **M7** CORS: `AllowAnyHeader().AllowAnyMethod().AllowCredentials()` to configured origins. Drop
  AllowCredentials if tokens are Bearer-only; tighten to actual headers/methods.
- **M8** Bootstrap admin: `Auth__SeedAdmin__Password` lingers in env/systemd; nothing detects
  the is_system account still must_change weeks later. Startup warning + document env-var removal.

### LOW

- **L1** Dev password grant `/auth/token` — keep bound to IsDevelopment(), never a config flag
  (ok today).
- **L2** Lockout flat threshold, no exponential backoff on repeat lockouts.
- **L3** — ✅ FIXED by PR7a. Failed-login counter incremented for already-locked accounts →
  attacker held the account locked indefinitely. `RecordFailedLoginAsync` is now a no-op while
  `locked_until` is in the future.
- **L4** No user notification on security events (admin reset, new-device login, lockout) — tied
  to absent email infra.
- **L5** `ForwardDefaultSelector`: junk `X-Api-Key` header suppresses a valid Bearer → 401. Minor.
- **L6** Login path ~6 sequential DB round trips; consolidate permission lookups like
  `ApiKeyRepository.GrantsAsync` (QueryMultipleAsync).

Files still worth pulling: ApiEndpointExtensions/ApiStartupExtensions (NetworkOptions), API key
endpoints file, CallerContextFactory, DB schema for platform_users / api_key / audit tables.

## Finding 5 — Organizations / Geography (HierarchyEndpoints.cs + repos)

Reviewed with a second pass 2026-09-01 — initial F5a–F5h refined below (F5f refuted, F5b partly
refuted).

### PR6 RESOLUTION (2026-09-07) — 5-H1, 5-H2, 5-M1 ✅

BA + dotnet-expert planned independently and converged. Code-only, no schema change.

- **5-H1** — `OrganizationRepository.RequireReachAsync` / `GeographyRepository.RequireAreaAsync`
  now take the `NpgsqlTransaction` and enrol every command in it (was executing on the open
  transaction's connection un-enrolled → Npgsql throws → 500 for **every** scoped `*.manage`
  caller on create and deactivate). The vestigial `tx`-always-null param is gone. 9 call sites
  updated — `RequireReachAsync` ×4 (`UpsertUnitAsync` ×2, `DeactivateUnitAsync` ×2) and
  `RequireAreaAsync` ×5 (`UpsertAreaAsync` ×2, `DeactivateAreaAsync` ×2, `UpsertSiteAsync` — the
  site path the finding undercounted).
  Secondary fix: `RequireReachAsync` switched from `has_permission()` (which false-denies a
  caller whose group also constrains geography — the PR2 bug, invariant 12) to
  `authorized_org_units` set-membership, matching `RequireAreaAsync` and `CameraRepository`.
- **5-H2** — both deactivate paths take one coarse `pg_advisory_xact_lock` per hierarchy
  (`hashtext('federation.organization_units.deactivate')` /
  `'federation.geographic_areas.deactivate'`), held for the transaction, plus
  `SELECT status … FOR UPDATE` on the root row and an idempotent early-return when it is already
  `INACTIVE`. Serializes concurrent deactivations (reparent is a `childStrategy` within
  deactivate, not a standalone op) and *orders* a direct-child INSERT (FK takes `FOR KEY SHARE`
  on the locked parent). With 5-M8 also fixed (below), the sequential re-attach path is closed,
  so the **only residual** is a narrow race: a grandchild inserted under a still-ACTIVE mid-tree
  node in the instant an ancestor is cascaded → ACTIVE under INACTIVE. Not a scope escape
  (`org_unit_descendants` / `authorized_org_units` ignore `status`); a reconciliation query in
  OPERATIONS.md finds and clears any. Create paths are deliberately **not** advisory-locked
  (would serialize every structural write in the estate). `// NOTE(5-H2):` comment in both
  repos; notes in DEPARTMENT-SCHEMA.md / GEOGRAPHY-SCHEMA.md / OPERATIONS.md.
- **5-M8** (folded into PR6 on the owner's call — both review agents recommended it, it makes
  the 5-H2 residual genuinely race-only) — new `RequireActiveParentAsync` (org) /
  `RequireActiveAreaAsync` (geo), run unconditionally in the caller's transaction, throw
  `InvalidReferenceException` (→ 400, existing handler) if the referenced parent is missing or
  not `ACTIVE`. Wired into `UpsertUnitAsync` (parent), `UpsertAreaAsync` (parent),
  `UpsertSiteAsync` (area), and both `Deactivate*` reparent branches (`newParentId`). A live
  node can no longer be attached under a retired one, concurrently or sequentially.
- **5-M1** — `HierarchyEndpoints.cs` `GET /organizations` and `/{id}` changed from
  `.RequirePermission("geography.read")` to `"organization.read"` (the repo's real gate). No
  description prose named a permission; no other route in the file mismatches.
- New `tests/Trinetra.IntegrationTests/HierarchyScopeTests.cs` — 19 tests, bespoke
  `HIER_SCOPE_TEST` role, caller scoped on **both** dimensions (so every guard branch runs and
  each test is also a `has_permission` false-denial regression) + a `HIER_READ_TEST` role with
  `organization.read` but not `geography.read` for 5-M1. Covers: scoped create/deactivate no
  longer 500s (org units, areas, sites), out-of-reach → `ForbiddenException`, reparent in/out of
  scope, already-INACTIVE deactivate is a no-op not a spurious children conflict (org + geo),
  5-M8 (create + reparent under an INACTIVE parent → `InvalidReferenceException`, org + geo +
  site), the advisory lock blocks a second transaction, and `ListAsync`/`GetAsync` work without
  `geography.read`. Sabotage-verified ×4 (revert tx→has_permission: 4 fail; disable INACTIVE
  early-return: 1 fails; remove advisory lock: concurrency test fails; disable 5-M8 check: 1
  fails). Build 0 warnings; 140 integration pass (+19); same 18 pre-existing `vendor_kind`
  failures.
- **NOT** pulled into PR6 (stay their own findings):
  5-M10 (the already-INACTIVE early-return still returns `null`, so the endpoint writes a
  status-change audit row and returns 204 for a no-op — pre-existing, PR6 newly routes the
  documented Refuse path through it; the `FOR UPDATE` SELECT is the hook for the eventual fix).
  `ForbiddenException` still maps via the global middleware (403), not 404 — unchanged.

Status: implemented + reviewed + fixed + tested, **NOT COMMITTED** (Jaydip commits via GitHub
Desktop).

### HIGH

- **5-H1** Scoped `*.manage` callers likely 500 on every create/deactivate.
  `OrganizationRepository.RequireReachAsync` (:263-283) and `GeographyRepository.RequireAreaAsync`
  (:293-312) run `ExecuteScalarAsync` on `work.Connection` WITHOUT `work.Transaction` (vestigial
  `tx` param, all callers pass null). Npgsql 9 throws when a command has no Transaction set on a
  connection with an open txn → 500. Only hits non-unscoped callers, so unscoped-admin tests miss
  it — ships broken for delegated/district-scoped admins. Fix: pass `work.Transaction`, drop `tx`.
  Needs integration test with a scoped CallerContext.
- **5-H2** TOCTOU race on deactivate/reparent. Read children → decide → UPDATE under READ
  COMMITTED, no `FOR UPDATE`, no re-check, no `updated_at`/version guard. Concurrent child-create
  leaves ACTIVE child under INACTIVE parent; concurrent cascade misses grafted subtree; two
  reparents = silent last-writer-wins. Lock subtree or re-assert child state in the write +
  optimistic guard.

### MEDIUM

- **5-M1** (was F5a) CONFIRMED but latent: `GET /organizations` + `/{id}` filter on
  `geography.read` (:31,:39); repo actually requires/keys on `organization.read`. Real gate is
  the repo so not an exposure, but a group with `organization.read` & not `geography.read` is
  wrongly 403'd at the filter, and OpenAPI advertises a false requirement. All seeded roles pair
  the two so latent. Fix both → `organization.read`.
- **5-M2** `area_type` unvalidated free text. No FK to `geographic_area_types`, no check in
  endpoint or `UpsertAreaAsync`, no `level_order` check vs parent — contradicts route description
  (":141-142") and DDL comment ("reject a district inside a village"). Validate in Storage.
- **5-M3** Codes globally unique estate-wide (`organizations/units/areas.code` bare UNIQUE).
  Two districts naming a ward `WARD-1` collide. ✅ PARTIAL (v1.11 PR): `geographic_areas.code`
  is now `UNIQUE NULLS NOT DISTINCT (parent_area_id, code)` (roots still unique among
  themselves); Admin `Lookup` rejects an ambiguous bare-code match. `organizations` /
  `organization_units` still globally unique — separate finding if it matters.
- **5-M4** (was F5c) — ⚠️ PARTIALLY FIXED (2026-09-07, owner asked for hierarchy edit
  endpoints). Added: `GET /api/v1/organization-units/{id}`, `PUT /api/v1/organizations/{id}`,
  `PUT /api/v1/organization-units/{id}` — field edits (code/name/type/status/description), the
  `update` path audits `before:`/`after:` the stored projection (not the request DTO), a unit
  cannot change organization, and a PUT that changes `parentUnitId` is 400'd (re-parent has its
  own locked `/deactivate` flow). New `OrganizationRepository.GetUnitAsync`. **Still open:**
  `GET`/`PUT` for **geographic areas** (GET/{id} exists, no PUT) and **sites** (no GET/{id}, no
  PUT), and `ListUnitsAsync` still returns `200 []` for a bogus/out-of-scope parent org rather
  than 404. Access-group edit folds into F7. See the CREATE→UPDATE audit note below.
- **5-M5** (was F5b) PARTLY REFUTED: create endpoints can't currently mis-record an update — DTOs
  carry no Id, always INSERT, duplicate code → 409. Real issues: (a) latent — the moment a PUT is
  added calling the same `Upsert*`, it audits `action:"create"` `before:null`; split
  create/update or have repo return inserted-vs-updated. (b) audit `after:` is the request DTO
  not the persisted row — omits generated id + server defaults, records client-sent Status;
  undercuts UoW snapshot intent.
- **5-M6** (was F5e) CONFIRMED + worse: `DeactivateAsync` writes ONE audit row on the parent
  (:254-257) — no `newParentId`, no list of moved children/sites, no cascade descendant list. A
  cascade taking 500 sites offline = 1 thin audit row. Record newParentId + affected id list.
- **5-M7** ✅ PR9 (opt-in `?page`, hard cap, `count(*) OVER()`).
- **5-M7 (orig)** (was F5g) CONFIRMED: `ListOrganizations/Units/Areas/Sites/AreaChildren` — `ORDER BY
  name` no LIMIT/cursor. Unscoped `ListAsync` sorts whole table. Codebase elsewhere mandates
  keyset pagination (`Contracts.cs:91-96`). Add level-slicing + keyset cursor, at least sites &
  areas.
- **5-M8** ✅ PR6. Live children can attach under an INACTIVE parent. None of
  `UpsertUnit/Area/Site` check parent `status='ACTIVE'`; reparent `newParentId` checked for reach
  + not-in-branch, never ACTIVE. Defeats the deactivation flow. FIXED in PR6 — see the PR6
  RESOLUTION block above (`RequireActiveParentAsync` / `RequireActiveAreaAsync` →
  `InvalidReferenceException` → 400).
- **5-M9** Over-length `code` → 500. `code` VARCHAR(50) (sites 100); 51 chars raises `22001`, not
  in `ConstraintViolationExceptionHandler` switch (→500). No length/charset validation on
  Code/Name/Address at endpoint. Add DTO validation and/or map `22001`.
- **5-M10** Phantom 204 + false audit on deactivating a nonexistent / already-INACTIVE node
  (unscoped caller): UPDATE hits 0 rows → null → still writes audit `before:{ACTIVE}`
  `after:{INACTIVE}` and returns 204. Check existence/current status → 404 / 409.

### LOW

- **5-L1** (was F5d) CONFIRMED: no reactivation endpoint. Add `POST /…/{id}/activate` w/ own
  permission + audit + parent-ACTIVE check.
- **5-L2** (was F5f) REFUTED: cycle/self-parent prevention exists as DB triggers
  (`assert_org_unit_acyclic` / `assert_geographic_area_acyclic`), incl. same-org rule for units;
  `ConstraintViolationExceptionHandler` maps to 400. App-side `wouldDetach` check is
  belt-and-braces. Adequate. (Reparent UPDATE relies on per-row trigger — fine, but see 5-H2
  race.)
- **5-L3** (was F5h) hard-delete absence is DEFENSIBLE + documented (units referenced by
  config_audit + connector_target). **IN SCOPE (2026-09-04):** add guarded
  `DELETE /api/v1/{sites|organization-units|geographic-areas|organizations}/{id}` — 204 only if
  no connector_target, no child rows, no config_audit rows other than its own create; caller has
  `*.manage`. 409 with the blocking-reference list otherwise (reuse deactivate conflict shape).
  Own audit row. Deactivate stays the normal path.
- **5-L4** `ListAreasAsync` with `rootsOnly=true` AND `parentId=x` → `parent IS NULL AND
  parent=@x` → always empty. Reject combo with 400.
- **5-L5** None of these mutation endpoints check `MustChangePassword` — confirm it's enforced by
  global middleware (ties to 4-M1).
- **5-L6** style: create handlers call `caller.Require("*.manage")` — dead given
  `.RequirePermission` filter + repo check; list handlers don't (inconsistent). `GetAncestorsAsync`
  opens two pool connections per request.

Scope model (invariant 12) — **PASSES**. Org/unit resolve purely on org dimension, geography
purely on geo dimension via separate `GeographyRepository.Geo(...)`; not cross-consulted. No
escalation-by-parent-attach: `UpsertUnit/Area` check node AND parent for scoped callers, reject
scoped caller creating a root. Invariant 10 structurally OK for happy paths (weaknesses are 5-H1
checks-outside-txn and 5-M6 audit granularity, not a missing audit).

### CREATE → UPDATE endpoint audit (2026-09-07, owner asked)

Every `POST` that creates a persistent entity, and whether it has a `PUT`/`PATCH` to edit by id:

| Entity | Create | Update | State |
|---|---|---|---|
| Camera | `POST /cameras` | `PUT /{id}` + `PATCH /{id}` | ✅ |
| VMS target | `POST /vms` | `PUT /{id}` + `POST /{id}/state` | ✅ |
| User | `POST /users` | `PUT /{id}` (username immutable — 6-L3) | ✅ |
| Maintenance record | `POST /cameras/{id}/maintenance` | `PATCH /.../maintenance/{recordId}` | ✅ |
| Credential | — | `PUT /` is the idempotent write | ✅ |
| **Organization** | `POST /organizations` | `PUT /{id}` | ✅ (added 2026-09-07) |
| **Organization unit** | `POST /organizations/{id}/units` | `GET`+`PUT /organization-units/{id}` | ✅ (added 2026-09-07) |
| **Geographic area** | `POST /geographic-areas` | — (`GET /{id}` exists) | ❌ open — 5-M4 |
| **Site** | `POST /sites` | — (no `GET /{id}`, no `PUT`) | ❌ open — 5-M4 |
| **Access group** | `POST /access-groups` | — (no `PUT`, no activate) | ❌ open — 6-H2 / F7 |
| **Watchlist entry** | `POST /watchlist` | — (no `GET /{id}`, no `PUT`) | ❌ open — new (F16-adjacent) |
| Detection / federation event | ingest | n/a — immutable observation | ✅ n/a |
| Connection test | `POST` | n/a — async job | ✅ n/a |
| API key | `POST /api-keys` | revoke + reissue; rotation endpoint = 8-M2 | intended |

Next: `GET`/`PUT` for geographic area + site (extend the 5-M4 PR), watchlist-entry edit (its own
item), access-group edit via F7.

## Finding 6 — Users & Access control (UserEndpoints.cs, AccessGroupEndpoints.cs)

Review 2026-09-04. Write paths are meticulously guarded (escalation chokepoint + lockout guards)
— the gaps are on the read side and in lifecycle.

### HIGH

- **6-H1** Read endpoints have ZERO scope enforcement. `UserRepository.ListAsync(ct)` /
  `GetAsync(id,ct)` / `ListGroupsAsync` / `GetPermissionsAsync` take no `CallerContext`
  (violates CLAUDE.md invariant 11). Any `user.read` holder sees every account estate-wide incl.
  `IsSystem`, `LastLoginAt`; `GET /users/{id}` has no `RefuseIfBeyondAuthorityAsync` (writes do).
  `GET /users/{id}/permissions` + `/groups` let a departmental user enumerate another admin's
  entitlements. Same for `AccessGroupRepository.ListAsync` / `ListMembersAsync` — `group.read`
  holder sees every group incl. SUPER_ADMIN and its members = a roster of platform admins. Recon
  surface. Scope reads the way writes are scoped, or at least gate single-resource reads through
  the authority check.

  **PR5 RESOLUTION (2026-09-06):** ✅ fixed.
  - `UserRepository.ListAsync` / `AccessGroupRepository.ListAsync` / `ListMembersAsync` and
    `ApiKeyRepository.ListAsync` now take a required `CallerContext` and filter in SQL.
  - Filter rule for groups/keys = the read-side mirror of `GroupGrantGuard` (new
    `AccessGroupRepository.GroupVisiblePredicate` fragment, both dimensions): visible iff the
    caller could confer it. A dimension-unrestricted group — PLATFORM-ADMINS included — is hidden
    from any caller scoped on that dimension. Filter rule for users = `CanAdministerAsync`
    conditions 2+3 (org dimension only) + `NOT is_system` (new
    `UserRepository.VisibleForUserReadPredicate`, reused by `ListMembersAsync` so a visible
    group's roster is filtered like the directory).
  - Single-row fetches (`GET /users/{id}`, `/{id}/groups`, `/{id}/permissions`,
    `GET /access-groups/{id}`, `/{id}/members`) gated in the endpoint via the new shared
    `Auth/UserAuthorityGuard` (extracted from `UserEndpoints.RefuseIfBeyondAuthorityAsync`) and
    `AccessGroupRepository.IsVisibleToAsync` → 404, never 403. Narrow invariant-11 exception
    documented in `AUTHORIZATION.md` §3 + `CLAUDE.md` #11.
  - **List and single-GET use the same reachability rule** (implementation-review fix): the read
    guard calls a new `UserRepository.CanReadAsync` — conditions 2+3 only, exactly
    `VisibleForUserReadPredicate` on one row — not the full `CanAdministerAsync`. A row that
    appears in `GET /users` never 404s on its own detail route. `CanAdministerAsync` (with its
    condition-1 permission-subset check) is now write-path only; it gained a `permission` param.
  - No schema change — "sees the whole estate" is already `IsUnscopedFor(<read perm>)`.
  - Dead `AccessGroupRepository.ListScopesAsync` (0 callers, no `CallerContext`) removed rather
    than left as a latent invariant-11 hole.
  - `docs/AUTHORIZATION.md` §3/§5/§7 + endpoint descriptions updated.
  - **Known asymmetry, split out:** user visibility is org-only (matches `CanAdministerAsync`);
    groups/keys check both dimensions. Geography for user authority folds into the F6 6-M1
    geo-asymmetry item.
  - **Operator note (API not yet deployed):** after this change a scoped administrator (e.g. a
    STATE_ADMIN placed in a scoped group) stops seeing users / groups / keys they saw before —
    the platform-admin group included. "Missing" rows post-upgrade are expected; the remedy is
    an unscoped read grant or a wider group.
  - Tests: `tests/Trinetra.IntegrationTests/UnscopedReadScopeTests.cs` (17, real Postgres,
    bespoke `READ_SCOPE_TEST` scoped role) — incl. geography-dimension group hiding, the
    `is_system` single-GET 404, list-vs-single-GET agreement (with a higher-priv in-reach user),
    and the unscoped-caller control. Sabotage-verified: removing the RevokeAsync predicate, the
    `NOT is_system` filter, and reverting the read guard to full `CanAdministerAsync` all turn
    tests red. `ApiKeyLifecycleTests` `AdminCaller` fixture made explicitly unscoped (REPORTING
    group has no scope). Build 0 warnings; same 18 pre-existing `vendor_kind` fails.
- **6-H2** No group lifecycle endpoints. Group created as DRAFT; route description says "add its
  scopes, then activate it" — **there is no activate route**. No PUT/PATCH, no `/{id}/activate`,
  no deactivate, no delete. Only path DRAFT→ACTIVE is `POST /` again with same code hitting
  `UpsertAsync ON CONFLICT (code) DO UPDATE` — audited as "create", create-shaped hack (same
  smell as 5-M5). Need: activate / suspend / delete-if-no-members, each with audit.
  *(Superseded by F7's approval state machine.)*

### MEDIUM

- **6-M1** Geography escalation asymmetry in scope add/remove. `AddScopeAsync` reach-checks only
  `scopeType=="ORGANIZATION"` (:215) — a scoped caller can add a `GEOGRAPHY` scope for a district
  outside their geographic reach. `RemoveScopeAsync` widening guard (:265) also only covers the
  last ORGANIZATION scope — removing the last GEOGRAPHY scope widens to every area unchecked.
  Add the symmetric geo checks (`IsUnscopedForGeography` / caller geo reach).
- **6-M2** `AddScopeAsync` no per-scope-type field validation: ORGANIZATION scope with null
  `OrganizationUnitId` (or GEOGRAPHY w/ null area, RESOURCE w/ null resourceType/Id) passes —
  could store a nonsense/over-broad scope row. Validate required field per type.
- **6-M3** `AddScopeAsync` RESOURCE scope: `ResourceType`/`ResourceId` unvalidated — no check the
  resource exists or is in caller's scope. RESOURCE scope naming a camera the caller can't see.
- **6-M4** TOCTOU on the lockout guards. `UpdateAsync` / `RemoveFromGroupAsync` run
  `CountOtherHoldersAsync("user.manage")` BEFORE `UnitOfWork.BeginAsync` — two concurrent
  deactivations of the last two admins both pass, both commit → platform locked out. Move the
  count inside the txn with row locking, or a partial unique / trigger backstop.
- **6-M5** Email unvalidated + not unique on `CreateAsync`/`UpdateAsync` — needed for F2 (forgot
  password) and F3 (SSO account linking). Add format check + unique constraint + (later)
  verification state.
- **6-M6** ✅ PR9 — `?page`/`?pageSize` on users / groups / members; `count(*) OVER()`.
- **6-M6 (orig)** No pagination on `ListAsync` (users), `ListAsync` (groups), `MembersAsync`. Same class
  as 5-M7. Thousands of users/members at scale.
- **6-M7** `CreateAsync` (group) and `AddScopeAsync` audit `after: request` — raw DTO incl.
  client-sent `Status`, not the persisted row (same fidelity note as 5-M5b).

### LOW

- **6-L1** Lockout guard only protects `user.manage`. Last holder of `group.manage` (or
  `group.manage` without `user.manage` — can build a SUPER group but not assign it) is also
  unrecoverable; no guard. Decide whether `group.manage` deserves the same protection.
- **6-L2** `CreateUserRequest` has no `Status` — can't create a pre-disabled account staged for
  later. Minor.
- **6-L3** Username immutable (`UpdateUserRequest` = DisplayName/Email/Status only) — confirm
  intended.
- **6-L4** Roles are read-only reference data — no create-custom-role endpoint. *(F7 changes this.)*
- **6-L5** `CreateAsync` (user) duplicate-username: `ExistsAsync` pre-check then insert — race
  falls to DB unique constraint; confirm `23505` maps to 409 not 500 in
  `ConstraintViolationExceptionHandler`.
- **6-L6** Self-assignment to a group (passes the 3 checks legitimately) has no audit distinction
  / no alert. Minor.

Not a finding: the `AddToGroupAsync` 3-check escalation chokepoint is well-built; permissions are
role-fixed at group creation so the assignment-time subset check stays valid; scope widening is
(partially, see 6-M1) guarded.

## Finding 7 — DESIGN DIRECTION: custom roles + maker-checker approval (2026-09-04)

> **STATUS (2026-09-08): SPLIT.** The **composed-roles CRUD** half is ✅ implemented in the
> v1.11 PR — new `role.manage` permission (delegable, owner reversed the SUPER_ADMIN-only call),
> `POST/PUT/DELETE /api/v1/roles`, presets editable in place (`SUPER_ADMIN` locked; presets
> not deletable; `roles.customized_at` guards against migration re-seed reverting edits), the
> same escalation guard as access-group creation. Access groups gained `PUT /{id}` +
> `/activate` + `/disable` (6-H2, 6-L4 closed). The **maker-checker approval workflow** half
> (`approval_request`, `role.approve`/`group.approve`, PENDING_APPROVAL state, org-unit-admin
> approvers) stays **DEFERRED** — owner wants the approver model designed separately.


A different RBAC shape than what's built:

1. **User-composed roles** — a user picks multiple permissions to form a role, rather than only
   choosing from static seeded roles. (Current: `roles` are seeded, `isSystem`, read-only via API
   — 6-L4.)
2. **Approval workflow** — a newly composed role, AND a newly created access group, must be
   approved before it grants anything. (Current: group is created `DRAFT` but there's no activate
   route at all — 6-H2 — and no approval concept; RBAC-LOGICAL-FLOW §25 only lists
   DRAFT→ACTIVE→DISABLED, no approver.)

**Why:** flexibility without pre-seeding every role; separation of duties on privilege grants.

**How to apply / open design questions to resolve before implementing:**

- One shared `approval_request` abstraction covering {custom role create, group create, maybe
  sensitive group-membership assignment} — not approval bolted separately onto each endpoint.
- Lifecycle: DRAFT → PENDING_APPROVAL → ACTIVE → DISABLED (+ REJECTED). Nothing grants access
  until ACTIVE.
- New permissions: `role.manage` (compose/submit), `role.approve`, `group.approve`. Approver
  ≠ submitter (enforced), audit both submit and approve/reject.
- Escalation rule for custom roles: submitter can only include permissions they themselves hold
  (reuse the existing subset check from `AddToGroupAsync`/group `CreateAsync`). Decide whether the
  approver may approve a role exceeding the approver's own permissions (probably yes — that's the
  point of a higher approver — but must be explicit + audited).
- Keep seeded `isSystem` roles as un-deletable, un-editable baseline; custom roles layer on top.
- Interaction with 6-H1 (read scoping), 6-H2 (this replaces the missing activate route), 5-M5 /
  6-M7 (upsert-as-create audit) — fold together.
- Note existing `access_group.status` DRAFT and `AccessGroupRepository.UpsertAsync` — the
  approval state machine supersedes the current create-shaped activation hack.

View: worth doing; the risk is scattering approval logic — build the request/approval model once
and route all three grant types through it.

### Owner's expanded model (2026-09-07) — hierarchy + RBAC, needs a design pass

Verbatim intent: *access group = role (composed of permissions) + organization unit + geographic
area; nothing is static except the permission catalogue (`vms.read`, `vms.manage`,
`geography.read`, …). Organization units can also be tied to a geographic area. Geographic-area
LEVELS are operator-defined and each level carries name + description; the last level is a site
by default, or sites can be removed entirely.*

Buildable now (done — see 5-M4): plain `PUT`/`GET` for organization + organization unit.

Needs decisions before building — each is a schema change:
- **Org-unit ↔ geographic-area link.** A nullable `organization_units.geographic_area_id`? What
  it MEANS (the unit "operates in" that area — a data attribute) vs what it must NOT do (collapse
  invariant 12 — org and geography stay independent scope dimensions, ANDed). Does a group scoped
  to that unit implicitly inherit the linked area, or stay explicit?
- **`description` on `organization_units` and `geographic_areas`.** Both only have `name` +
  `metadata` JSONB today. The owner wants first-class `name` + `description` per level.
- **Sites optional / removable.** Large: cameras reach geography via a site
  (`connector_target.site_id`, `federated_camera.site_id`, CAMERA-SCHEMA). Making the deepest
  area level the leaf and dropping sites means cameras attach directly to an area — a Model 1
  registry migration. Decide: keep sites mandatory, make them optional (nullable, area as
  fallback), or remove.
- **User-composed roles** — this IS finding 7 above. Permission catalogue stays seeded; `roles`
  become user-built compositions gated by the subset check; `approval_request` for role + group.

Recommendation: one design doc covering the hierarchy changes + F7 together (they share the
"nothing is static" premise), reviewed with the owner, before any of it is built.

### DESIGN REVIEW DONE + OWNER DECISIONS (2026-09-08, BA + dotnet-expert)

Both agents converged. Owner decisions:

- **D1 Group scope cardinality** — KEEP many-scopes-per-group in the schema (`group_scopes` unchanged).
  The "role + one org + one geo" shape is the default CREATE FORM, not a constraint. A police
  estate needs multi-scope groups (cross-district investigation cells, task forces spanning
  departments). Add a denormalised "primary org unit / primary geo area" per group for display +
  SSO claim-mapping.
- **D2 Org-unit ↔ geographic-area link** — nullable `organization_units.geographic_area_id`,
  ONE area, **DESCRIPTIVE ONLY**. Never read by `has_permission` / `authorized_*` / any scope
  predicate (would collapse invariant 12 — a unit HQ'd in one area legitimately owns a camera in
  the next). Used only as a group-create suggestion + a map pin. Review guard: nothing joins it
  in a scope predicate.
- **D3 Sites** — **REMOVED ENTIRELY (hard cut, 2026-09-08).** Owner: "remove site id, geographics
  purely work on dynamic, not want to give static value" — the geo hierarchy is fully
  operator-defined levels, no fixed bottom tier. `DROP TABLE sites`; `site_id` → `geographic_area_id`
  on `cameras` (NOT NULL, backfilled from `sites`), `connector_target` / `federated_camera` /
  `federation_event` / `detection_event` (nullable, backfilled). Site fields (address, lat/long,
  site_type) **dropped** — cameras carry their own coordinates; areas stay pure administrative
  containers. A camera attaches to a geographic area at **ANY level** (owner: "any area and any
  level with organization unit, so it can find easily") — no leaf-only rule. No `resolved_area()`
  helper, no XOR CHECK — a camera just has `geographic_area_id` directly like it has
  `organization_unit_id`. The geo-scope predicate in ~8 repos simplifies from a `sites` sub-select
  to `X.geographic_area_id`. `GET/POST /api/v1/sites` routes + `GeographyRepository` site methods +
  `Site` model + `Site*` DTOs deleted. `PostgresFixture.ResetTargetsAsync` site seed removed.
- **D4 `description` per level** — plain `TEXT NULL` on `organization_units`, `geographic_areas`,
  `geographic_area_types`. Per-level custom-field definitions deferred (use `metadata` JSONB).
- **D5 `area_type`** — promote from free text to a REQUIRED FK into `geographic_area_types`;
  add level-order containment to `assert_geographic_area_acyclic()` (child level_order > parent).
  Backfill the registry from distinct existing `area_type` values.
- **D6 Composed roles + approval (F7)** — **DEFERRED**. Owner wants to work through the approver
  model separately (org-unit-admin-as-approver and other cases). `role.compose` / `role.approve`
  / `group.approve` would be SUPER_ADMIN-only initially when it does land.
- **Build order** — ONE migration / PR for the whole hierarchy change (D1 UI part, D2, D3, D4,
  D5 + finish 5-M4 `GET`/`PUT` for geographic areas). PR7.5 (org + unit `PUT`/`GET`, already
  built, uncommitted) folds into it.

Resolver hot path: unchanged and slightly *simpler* — the geo predicate drops the `sites`
sub-select for a direct `geographic_area_id` column. No security-function signature changes →
invariant 13 not triggered. Migration: `v1.11.sql`; additive except the `sites` teardown; seeded
roles / existing groups / existing scopes untouched. API not deployed → the destructive `DROP
TABLE sites` + `DROP COLUMN site_id` is acceptable.

## Finding 8 — API keys (ApiKeyEndpoints.cs + ApiKeyRepository.cs)

Review 2026-09-04, VERIFIED by second pass.

### CRITICAL

- **8-C1** CONFIRMED. **`apikey.manage` alone = full estate takeover.** `CreateAsync` uses
  `caller` only for `created_by`. None of the 3 `AddToGroupAsync` guards (perm-subset /
  `HasOrganizationScopeAsync` / `GroupScopesWithinReachAsync`) apply. `apikey.manage` is granted
  to `STATE_ADMIN` (v1.3.sql:48-55) — a scoped role. Scenario: Gujarat STATE_ADMIN POSTs
  `{groupId: <PLATFORM-ADMINS id>}` → raw key authenticating as unscoped platform admin over all
  80k cameras. Endpoint doc even claims "acts exactly as a user does" — the behaviour that isn't
  enforced. Fix: extract the 3-check chokepoint to a shared helper, run it here; key's effective
  perms/scope ⊆ creator's.

### HIGH

- **8-H1** CONFIRMED. `RevokeAsync` CTE matches `WHERE id=@id` only; `caller` → `revoked_by` only.
  Any `apikey.manage` holder revokes any key estate-wide (enumerate via 8-H2, then kill the
  AI-worker key or another state's integration → uniform 401 → cross-dept outage).
- **8-H2** CONFIRMED; "deliberate" justification NOT defensible. `ListAsync(ct)` no CallerContext,
  no filter. Every `apikey.read` holder (incl. scoped STATE_ADMIN) sees every key's name, **bound
  group code (= privilege level of every service account)**, expiry, last-used, revoked state —
  a target list for 8-H1. Every other list endpoint in the codebase is scoped. Fix: restrict to
  `IsUnscopedFor("apikey.read")` or filter by key's group org-scope within caller reach.

**PR5 RESOLUTION (2026-09-06):** ✅ 8-H1 + 8-H2 fixed together (they must share one predicate or
revoke-what-you-cannot-see bypasses the fix).
- `ApiKeyRepository.ListAsync` takes a required `CallerContext`; SQL gains
  `AccessGroupRepository.GroupVisiblePredicate` keyed on `apikey.read` (a key's "scope" = its
  group's scope). `ApiKeyEndpoints.ListAsync` gains `HttpContext`.
- `ApiKeyRepository.RevokeAsync` — the `target` CTE gains the same predicate keyed on
  `apikey.manage`, and the `upd` CTE only fires `WHERE ... AND EXISTS (SELECT 1 FROM target)`, so
  an out-of-reach key is returned as `RevokeOutcome.NotFound` (→ 404, already wired) **and left
  untouched**. No new enum value.
- `GroupVisiblePredicate` intentionally does **not** filter `ag.status`: a key (or a caller)
  bound to a group later set DRAFT/DISABLED stays visible and revocable to whoever reaches the
  group's declared scopes — you must be able to see a stale grant to revoke it. (The user-side
  `VisibleForUserReadPredicate` does require `ag.status = 'ACTIVE'`, but only in its
  "held-through-an-estate-wide-group" sub-check, where a non-active group correctly grants
  nothing.)
- Full detail + tests under the 6-H1 PR5 RESOLUTION block above.
- **8-NEW-H** (second pass) `TouchAsync` — `HandleAuthenticateAsync` awaits
  `UPDATE api_key SET last_used_at=now()` on EVERY authenticated M2M request, on a 3rd connection
  (after FindAsync + GrantsAsync), write+row-lock on one hot row per key. AI-worker fleet vs 80k
  cameras authenticates constantly. Cache grants briefly; make last_used_at best-effort / coarse
  (only if older than N min).

### MEDIUM

- **8-H3** ✅ **FIXED.** `GroupGrantGuard.CheckAsync` (shared by `ApiKeyEndpoints.CreateAsync`
  and `UserEndpoints.AddToGroupAsync`) rejects a non-ACTIVE group with **409** — ahead of the
  unscoped short-circuit, so even a platform admin cannot bind a key or membership to a
  `DRAFT`/`INACTIVE` group. Landed in `2504dde` but was never marked; regression test
  `GroupGrantGuardTests` (4, sabotage-checked) + doc-comment refresh (post-v1.12
  `DISABLED`→`INACTIVE`) added 2026-09-08 (uncommitted). The old note below (500→400, FK
  violation) was the *unfixed* behaviour.
- **8-M1** CONFIRMED. `ExpiresAt` optional, `api_key.expires_at` nullable, `FindAsync` treats
  NULL = never expires. No server max. Cap + require + near-expiry warning.
- **8-M2** CONFIRMED. No rotation endpoint / overlap window → pushes people to non-expiring keys.
- **8-M3** CONFIRMED. `Created(Location=/api/v1/access-groups/{groupId})` — wrong resource; no
  `GET /api-keys/{id}`.
- **8-M5** ✅ PR9 — `?page`/`?pageSize` on `/api-keys`.

### LOW / REFUTED

- **8-M4** REFUTED as a 500 risk — `config_audit.action` is `TEXT NOT NULL`, no CHECK. `"revoke"`
  inserts fine. Consistency nit only (everything else uses create/update/delete); normalise if a
  reporting query or CHECK is ever added.
- **8-NEW-L** raw key serialised in 201 body — add explicit "never log this response" guard if any
  response-body logging middleware is ever introduced.
- **8-NEW-L** `api_key.display_name VARCHAR(255)` — over-long → `22001` unmapped → 500. Add DTO
  length validation.
- Key generation CONFIRMED sound: `RandomNumberGenerator.GetBytes(32)` (256-bit) hex, hashed in
  endpoint, only hash persisted, raw returned once. `keyId` truncation cosmetic (non-secret
  label); collision → 409 not 500. The earlier 4-H6 fully refuted. (4-H5 rate-limit gap on the
  API-key *auth* path still stands — separate concern, ties 8-NEW-H.)

## Finding 9 — VMS / connector targets (VmsEndpoints.cs + ConnectorTargetRepository.cs)

Review 2026-09-04, VERIFIED by second pass. Repo scoping better than API keys but the geography
dimension is broken across read AND write.

### HIGH

- **9-NEW-H** (second pass) **`PUT /vms/{id}` silently resets `state` to `Active`.** `TryBuild`
  never sets `State` → `target.State` = model default `TargetState.Active` (`ConnectorTarget.cs:60`);
  `UpsertAsync` does `ON CONFLICT DO UPDATE SET state=EXCLUDED.state`. Every full-replace flips
  the target to Active — directly contradicts the endpoint doc ("Does not change the target's
  state"). Scenario: a `Quarantined` target (bad credential, was locking the integration account)
  — operator edits `displayName` via PUT → target goes Active → workers re-claim it →
  bad-credential retry loop resumes estate-wide. Violates CLAUDE.md non-negotiable #4. Fix:
  preserve existing state in `ReplaceAsync` (read-before-write already happens).
- **9-H1** CONFIRMED + read path also broken. Write (`UpsertAsync`/`SetStateAsync`/`DeleteAsync`):
  org-only `has_permission(p_organization_unit_id=>...)`, no `p_geographic_area_id`, no
  `IsUnscopedForGeography` (called nowhere in either file). `GetAsync` DOES AND geography — BUT
  the whole predicate is short-circuited by `@Unscoped OR ...` where
  `@Unscoped = IsUnscopedFor("vms.read")` = the **organization** flag → an org-unscoped,
  geo-scoped caller bypasses the geo check (invariant 12 violation exactly). `ListAsync` +
  `OverviewAsync`: `authorized_org_units` only, no geography clause at all. Scenario: caller with
  `vms.create` org-unscoped (or scoped to unit X) but geo-confined to district A registers target
  with orgUnit=X (in reach) + siteId in district B → owns that target + all its cameras/events
  in B. Camera-registry + reconciliation code guard this correctly; VMS registry doesn't.
- **9-H2** CONFIRMED partial. Hard cascade `DELETE` with no state/lease guard. Lease is INLINE
  columns on `connector_target` (`leased_by`, `lease_expires_at`) — no orphan there. Cascade
  covers connector_capability/cursor/federated_camera/connector_health/connection_test. NOT
  cascaded + silently orphaned: `camera_status_history` (no FK) — and the endpoint doc only
  mentions events surviving, not this. Add `state <> 'Active'` precondition → 409; document the
  camera_status_history orphan.
- **9-H3** CONFIRMED. `MapDelete(...).RequirePermission("vms.update")`. Anyone who can edit a
  poll interval can permanently destroy the target + full inventory + capability matrix + health
  history. Add `vms.delete` (cf. `camera.delete` exists in v1.6).

### MEDIUM

- **9-M1** CONFIRMED as a design hazard; exact STJ behaviour NEEDS A TEST. `ConnectorTargetRequest`
  is a positional record with `bool VerifyTls = true` — STJ .NET 7+ *should* use the ctor default
  when the field is absent, but an explicit `"verifyTls": false` on a full-replace PUT silently
  disables cert verification with no confirm step, and null/wrong-typed input takes a different
  path. Make it `bool?` + reject omission, or require explicit acknowledgement to set false.
  Round-trip test before relying on the default.
- **9-M2** CONFIRMED. `TryBuild` validates vendor/endpoint-syntax/runtime-class only. No check
  orgUnit/site exist / are ACTIVE / site's `geographic_area_id` consistent with the org unit. FK
  → 400, but a target can attach to a DEACTIVATED unit/site (FK ignores status) → drops out of
  scope resolution silently.
**PR3 RESOLUTION (2026-09-04):** 9-NEW-H, 9-H2, 9-H3, 9-M1, 9-M2 all fixed.
- 9-NEW-H: `state = EXCLUDED.state` removed from `UpsertAsync`'s `ON CONFLICT DO UPDATE` — state
  is now write-once-at-insert, changed only by `SetStateAsync` thereafter.
- 9-H2: `RemoveAsync` refuses with 409 when `before.State == Active` (only Active blocks — a
  quarantined target is already not polled, so no in-flight-write race). `camera_status_history`
  needed no fix — its own DDL comment says "No FK on purpose" and it already ages out under the
  existing 90-day retention job; documented in the route description instead.
- 9-H3: new `db/versions/v1.7.sql` — `vms.delete` permission, granted to `STATE_ADMIN`,
  `DEPARTMENT_ADMIN`, `VMS_ADMIN` (+ SUPER_ADMIN backfill). `RemoveAsync`/`DeleteAsync` switched
  from `vms.update`; `SetStateAsync` deliberately left on `vms.update` (quarantine is edit-class).
- 9-M1: kept `VerifyTls` as non-nullable `bool` (NOT `bool?` — would violate the endpoint's own
  documented full-replace-not-patch contract). Proved via a real STJ round-trip test (using the
  app's actual `[FromBody]` options — `JsonSerializerOptions.Default` alone is a DIFFERENT,
  case-sensitive code path and would have proven nothing) that omission → `true` (safe default),
  explicit `false` → honoured, explicit `null` → 400 via the existing `BadRequestExceptionHandler`.
- 9-M2: new `InvalidReferenceException` → `InvalidReferenceExceptionHandler` (400), unconditional
  (not just for scoped callers — closed a gap where unscoped admins got zero validation) check in
  `UpsertAsync` that `organizationUnitId`/`siteId` exist and are `ACTIVE`. Narrowed to VMS only —
  the org/site hierarchy-consistency check and `CameraRepository`'s identical gap are explicitly
  deferred to a follow-up PR.
- New tests: `tests/Trinetra.IntegrationTests/VmsLifecycleTests.cs` (9 tests, real Postgres) +
  `tests/Trinetra.UnitTests/ConnectorTargetRequestSerializationTests.cs` (3 tests). Caught a real
  regression before it shipped: `GeographyScopeTests`'s bespoke test role didn't hold the new
  `vms.delete` permission, so its existing `Vms_DeleteAsync_OutOfDistrictTarget_AffectsNoRows`
  test started throwing `ForbiddenException` instead of returning false — fixed by extending that
  role's permission list.
- **Post-implementation review (BA + dotnet-expert) fix:** `VmsEndpoints.Redact(ConnectorTarget)`
  omitted `State` — the delete audit row for 9-H2 couldn't show what state the target was removed
  in, the one fact that guard exists to protect. Added.
- **Accepted test-coverage gap (BA-flagged, not fixed):** the 9-H2 409-on-Active guard lives in
  `VmsEndpoints.RemoveAsync` and is not exercised by any test — the repo has no
  `WebApplicationFactory`/HTTP-level test harness anywhere yet, so nothing in this PR introduces
  one for a single assertion. `VmsLifecycleTests.Delete_ActiveTarget_HasNothingRepositoryLevelToStopIt_GuardLivesInTheEndpoint`
  is honestly named — it proves the precondition (`GetAsync` reports true state) the guard reads,
  not the guard's 409 itself. Revisit if/when an HTTP-level test harness is added for another
  reason.
- **9-M3** ✅ PR9 — `?page`/`?pageSize` on `/vms/{id}/cameras`, hard cap 2000.
- **9-M3 (orig)** CONFIRMED. `CamerasAsync` → `SELECT ... FROM federated_camera WHERE target_id=@t
  ORDER BY native_camera_id` no LIMIT; one aggregating VMS = thousands of cameras, unbounded
  response, double array-materialised. (Target `ListAsync` also unpaginated but bounded by design
  — camera list is the real one.)
- **9-M4** CONFIRMED info leak. `CapabilitiesAsync`: out-of-scope → 404 "Not found"; in-scope
  unprobed → 404 "Not probed yet". Distinguishable bodies → probe which ids exist. Every sibling
  endpoint returns bare `TypedResults.NotFound()` for both. Fix the lone inconsistency.
- **9-NEW-M** (second pass) `RegisterAsync` audit passes `organizationUnitId:
  request.OrganizationUnitId` — caller-supplied, and `UpsertAsync`'s org check only proves
  *reachability* not correctness. Not exploitable (FK fail rolls back) but the audit org
  dimension trusts unverified input on the success path.

### LOW / OK

- **9-M5** CONFIRMED FINE — duplicate `code` → UniqueViolation → 409 "Already exists". No action.
- **9-L1** CONFIRMED + `SetStateAsync` also passes `organizationUnitId: null` (unlike
  create/update/delete which pass the target's unit) → state-change audit rows lose the org
  dimension for filtering. Plus `before: null`.
- **9-L2** CONFIRMED. Unknown `nativeCameraId` → `200` empty, not 404 (only the target's
  existence is checked).
- **9-L3** CONFIRMED + `SetStateAsync` unconditionally sets `leased_by=NULL,
  lease_expires_at=NULL` on EVERY call incl. Active→Active → force-releases a healthy target from
  its worker mid-cycle.
- **9-L4** CONFIRMED. `vendor` change on a live target with a discovered inventory — no guard; a
  different adapter then runs against the old vendor's native ids, corrupting `federated_camera`.
- **9-NEW-L** `CapabilitiesAsync` does
  `JsonSerializer.Deserialize<Dictionary<string,string>>(notes)` with no try/catch —
  worker-written malformed JSON → unhandled `JsonException` → 500.
- **9-NEW-L** `TryBuild` endpoint check accepts any parseable absolute URI (`file://`,
  `gopher://`); only the `http`-prefix branch adds a scheme. SSRF reachability depends on adapters
  (out of scope) but the advertised boundary check is weaker than it reads. Restrict to
  http/https.

## Finding 10 — Cameras / GIS / Health / Reconciliation

Review 2026-09-04 (CameraEndpoints.cs, GisEndpoints.cs, CameraHealthEndpoints.cs,
CameraReconciliationEndpoints.cs + CameraRepository.cs). **This cluster is markedly higher
quality** — proper opaque-cursor keyset pagination, exhaustive field validation, `Redact` audit
projections, and `CameraRepository` is the *reference* scope implementation: `ScopeArgs` uses
BOTH `IsUnscopedFor(perm)` AND `IsUnscopedForGeography(perm)` per-permission per-dimension, and
`RequirePlacementAsync` re-checks both dimensions on a move (this is what VMS 9-H1 should copy).
Findings are mostly M/L.

### MEDIUM

- **10-M1** **GIS permission bypass.** `GET /gis/cameras` (`FeedAsync`) is gated `gis.read` only,
  but `?includeSectors=true` computes and returns the exact coverage geometry that
  `GET /cameras/{id}/coverage` and `/gis/coverage` gate behind `gis.coverage.read`. `gis.read` +
  `includeSectors=true` == `gis.coverage.read`. Fix: require `gis.coverage.read` when
  `includeSectors` is set, or fold the two permissions.
- **10-M2** ✅ PR9 — feed stays a `FeatureCollection`; `?page`/`?pageSize` + `X-Total-Count` / `X-Result-Capped` / `X-Page` headers; no more silent drop.
- **10-M2 (orig)** GIS feed silently truncates. `FeedAsync` `FeedLimit = 5000`, no cursor — a dense
  2°×2° bbox with >5000 in-scope cameras drops rows with no `nextCursor`, no `truncated` flag, no
  error. The camera list endpoint paginates properly; the map source just loses data. Add a
  truncation signal or paginate.
- **10-M3** `NullableString` (CameraEndpoints.cs:712-721) silently **truncates** over-length
  values (`s[..max]`) instead of erroring — a 60-char `ipAddress` PATCH becomes a corrupt 45-char
  string. `RequireString` errors on over-length; the nullable variant corrupts. Make it error too.
- **10-M4** `BulkImportAsync` — up to 500 rows, each its own `BeginAsync`/`CommitAsync` executed
  sequentially inside one synchronous HTTP request → many seconds holding the request open, no
  timeout guard. Consider a background job above some row threshold.
- **10-M5** `BulkImportAsync` upsert-replace path audits `before: null` (line 242) — prior state
  not captured, unlike the single `ReplaceAsync` which does `Redact(before)`. Audit fidelity gap
  on the bulk path.
- **10-M6** Pagination anomaly with reused codes. `ListAsync` keyset cursor = base64(`cameraCode`).
  Retiring a camera frees its code for reuse (documented), so with `includeRetired=true` two rows
  can share a code → keyset on code alone can skip or loop. Add a tiebreaker (id) to the cursor.
- **10-M7** `ReconcileAsync` doesn't check the registry camera and the VMS-reported camera are
  geographically/organizationally consistent — you can link registry camera A (unit X, district
  D1) to a VMS camera physically in district D2. Caller-reachability of both is checked; mutual
  consistency isn't. Reconciliation-correctness issue.
- **10-M8** `FeedAsync` builds properties via `JsonSerializer.SerializeToElement` per-property
  per-camera — 5000 cameras × 13 props = ~65k JsonElement allocations on a map-render hot path.
  Project to a typed shape or serialise once.

### LOW

- **10-L1** `RetireAsync` — `reason` is a query-string param (`DELETE /cameras/{id}?reason=`),
  optional, unvalidated length. Health override *requires* a reason; retire doesn't. Inconsistent;
  put it in the body and consider requiring it.
- **10-L2** `OverrideAsync` (health) is gated `camera.update`, not a health-specific permission —
  a holder of `camera.health.read` can't override, but anyone who can edit optics/location can.
  Consider `camera.health.update`.
- **10-L3** `installationDate` patch uses `prop.Value.TryGetDateTime` → `DateTime`, not
  `DateTimeOffset` (CameraEndpoints.cs:633) — check `Camera.InstallationDate`'s type against
  CLAUDE.md non-negotiable #6 (probably a `date` column, but flag it).
- **10-L4** `ReconcileAsync` audit passes `organizationUnitId: null` (line 121) — loses the org
  dimension for audit filtering; `FromFederatedAsync` correctly passes `camera.OrganizationUnitId`.
  Inconsistent (same class as 9-L1).
- **10-L5** `CoverageAsync` — 204 for in-scope-camera-without-optics vs 404 for
  out-of-scope/absent lets a caller distinguish existence (same class as 9-M4, milder).
- **10-L6** `ListAsync` cursor isn't authenticated — a crafted base64 string is accepted (bad
  base64 silently restarts from page 1). Results stay scope-filtered so no data exposure; worst
  case a client skips ahead in its own result set. Low.
- **10-L7** `vmsId` on a camera write (`CreateAsync`/patch/from-federated) isn't validated to be a
  real, in-scope connector target. It's a soft reference (reconciliation is the real link) — low.

Not a finding: camera write scoping (both dimensions, per-permission, move re-checked), cursor
pagination, field validation, audit redaction — all correct. Use `CameraRepository` as the
template when fixing 9-H1 and the unscoped-read findings elsewhere.

## Finding 11 — Registry/GIS endpoints are NVR-unaware (2026-09-04)

Connector layer handles an NVR well (one `POST /vms`, one poll discovers all channels,
`GET /vms/{id}/cameras` = whole inventory). Model 1 registry + GIS treat every channel as an
independent asset — all per-channel, no NVR-aware batch path:

- **11-1** No "onboard all channels behind this NVR" flow. `POST /cameras/from-federated` is one
  call per native camera and each requires `cameraCode` + `cameraType` + lat/long in the body
  (NVR won't report per-camera GPS → `fed.Latitude` null → 400). 32 channels = 32 hand-built
  calls or a self-assembled bulk-import. No bulk-reconcile, no auto-match by name/code.
- **11-2** `GET /cameras` has no `vmsId` / `targetId` filter (only org/site/area/type/status/bbox/q).
  No first-class "registry cameras behind NVR X" view — only the federated view gives that.
- **11-3** Two divergent health surfaces for a reconciled channel: registry
  `operationalStatus`/`connectivityStatus` (manual / `PATCH /cameras/{id}/health`) is NOT fed by
  the connector; worker updates `federated_camera.health` separately. Registry can say "healthy"
  while `/vms/{id}/cameras` says "unreachable". Nothing reconciles them.
- **11-4** GIS = one pin per channel; if channels are given the NVR's own coordinates they stack
  with meaningless sectors. No co-location warning.
- **11-5** Dangling links on teardown: `DELETE /cameras/{id}` (retire) doesn't touch
  `federated_camera` — `camera_id` link stays pointing at a retired row. `DELETE /vms/{id}` (9-H2)
  cascades `federated_camera` away, leaving registry rows with a dead `vmsId` + orphaned
  `camera_status_history`.

Decision needed: is per-channel manual onboarding acceptable for the 100+ camera first phase, or
does an NVR need a "bulk adopt discovered channels" endpoint + a `targetId` list filter + health
propagation from federated→registry for reconciled cameras.

## Finding 12 — API key strength & non-disclosure (2026-09-04)

**VERIFIED OK:**

- Strength: `rawKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))` — 256-bit
  CSPRNG, generated server-side in the endpoint (ApiKeyEndpoints.cs:73). Only `SHA256` hex of it
  is persisted (`key_hash`); auth re-hashes + compares (ApiKeyAuthenticationHandler.cs:54-57).
  Raw value returned exactly once in the 201 body (`ApiKeyCreatedResponse.RawKey`).
- GET non-disclosure: `ApiKeyResponse` = `Id, KeyId, DisplayName, GroupId, GroupCode, CreatedAt,
  ExpiresAt, LastUsedAt, RevokedAt` — `KeyId` is the public `ak_…` label (16 chars, non-secret),
  NOT the secret. `ListAsync` never selects `key_hash` or raw material. No `GET /api-keys/{id}`
  exists at all. Creation response is the only place material appears. Clean.

OUTSTANDING (already logged, don't re-solve): 8-NEW-L add an explicit "never log this response
body" guard; 8-M1 no max lifetime; 8-H2 `ListAsync` leaks group/privilege metadata (not the key
itself). Note: unsalted SHA-256 for `key_hash` is acceptable ONLY because the secret is 256-bit
random — keep that invariant if the entropy is ever reduced.

## Finding 13 — Credentials (CredentialEndpoints.cs)

Review 2026-09-04. Design is strong: route is `/vms/{id}/credential` (scope by construction, no
flat caller-supplied reference), `/resolve` is the only secret-returning route, gated on
`credential.resolve` (held only by DETECTION_WORKER), audit-or-withhold (503 on audit failure),
AES-256-GCM app-side sealing.

### HIGH

- **13-H1** All three routes (`WriteAsync`/`ResolveAsync`/`StatusAsync`) scope via
  `ConnectorTargetRepository.GetAsync(id, caller)` — which inherited the **9-H1 geography bypass**
  (its geo predicate was short-circuited by the *organization* unscoped flag). So an org-unscoped,
  geo-scoped caller with `credential.write` could **overwrite the device password** of a target
  outside their geography — the doc itself calls this "the highest-privilege action in the
  system". **FIXED transitively by PR2's 9-H1 fix** — `GetAsync` now ANDs an independent geo flag.
- **13-H2** Blast radius of 8-C1: SUPER_ADMIN holds every permission incl. `credential.resolve`
  (v1.5.sql backfill). 8-C1 (mint a key in PLATFORM-ADMINS) → `credential.resolve` estate-wide →
  `GET /vms/{id}/credential/resolve` for every target → **exfiltrate every device credential in
  the estate**. Adds urgency to 8-C1.

### MEDIUM

- **13-M1** Verify `SecretWriter.WriteAsync` writes a `config_audit` row (invariant 10) — the
  endpoint itself does no `work.AuditAsync`; it relies entirely on the writer. "Highest-privilege
  action" must appear in the main audit trail, not only a credential-specific log.
- **13-M2** No delete-credential endpoint — you can overwrite but not remove. A decommissioned
  target's sealed secret lingers. Ties to retention.

### LOW

- **13-L1** Two concurrent `WriteAsync` on one target — last-writer-wins on the sealed secret, no
  version guard. A rotation racing a stale rotation interleaves silently.
- **13-L2** `request.Username` optional (only "password or token" enforced) — intentional for
  token-only devices; note.

## Finding 14 — Events (EventEndpoints.cs)

Review 2026-09-04. Small, tight, well-designed: `from`/`to` required + max 7 days, opaque keyset
cursor on `(occurred_at DESC, event_id)` WITH tiebreaker (unlike 10-M6), clamped page size,
explicit OpenSearch boundary.

### MEDIUM

- **14-M1** Verify `EventQueryRepository.QueryAsync` ANDs BOTH org + geography per-permission
  (like `CameraRepository`, not the VMS 9-H1 bug). `federation_event` carries denormalised org
  unit + geo area (per CLAUDE.md); confirm both are enforced and neither dimension reuses the
  other's unscoped flag.
- **14-M2** `event.acknowledge` is seeded (v1.sql) but has **no endpoint** — normalized
  federation events cannot be acknowledged. Watchlist alerts have their own `alert.acknowledge` +
  `POST /watchlist/alerts/{id}/acknowledge`. Either `event.acknowledge` is a dead seeded
  permission or there's a missing `POST /events/{id}/acknowledge`. Decide.

### LOW

- **14-L1** No `severity` / `minConfidence` / `vendorEventType` filters despite those fields being
  in `EventSummary` — a "show me WARN+ events" query isn't expressible. Enhancement.
- **14-L2** No `GET /events/{id}` single fetch — an event that scrolled past a cursor page can't
  be re-fetched without re-deriving the window. Minor.
- **14-L3** `cameraId` filter is an unvalidated string (format — GUID? `targetId:nativeId`? —
  undocumented). Dapper-parameterised so no injection; just unclear.
- **14-L4** Demo-path gap (not a code finding): "search metadata" / historical + free-text event
  search is deferred to OpenSearch which isn't built — only the 7-day hot window is queryable
  today.

## Finding 15 — Detections (DetectionEndpoints.cs + DetectionRepository.cs)

Review 2026-09-04.

### HIGH

- **15-H1** **Arbitrary file write via `request.Id` (path traversal).** `DetectionEventRequest.Id`
  is a client-supplied `string` (Contracts.cs:147). `ResolveSnapshotReferenceAsync` does
  `fileName = $"{request.Id}.jpg"` → `Path.Combine(root, fileName)` →
  `File.WriteAllBytesAsync(path, Convert.FromBase64String(base64))`. `request.Id =
  "../../etc/cron.d/x"` (or any traversal) writes attacker-controlled bytes to an arbitrary path
  as the service account. Needs `observation.write` — but the DETECTION_WORKER key lives on the
  AI-worker box and is exactly the credential that leaks. Fix: validate `Id` is a GUID / safe
  charset, generate the filename server-side, and assert the resolved path is under the root
  (`Path.GetFullPath` + prefix check).
- **15-H2** `Convert.FromBase64String(base64)` — no try/catch (malformed → 500) AND no size limit.
  A worker (or anyone with the key) POSTs a 500 MB base64 blob → decoded fully into memory +
  written to disk. No `MaxRequestBodySize` on the route. DoS / disk-fill.
  **✅ FIXED (2026-09-08, uncommitted).** New `DetectionEvidence.TryDecode` — rejects on the
  *encoded string length* (vs `Evidence:MaxSnapshotBytes`, default 4 MiB) before allocating,
  decodes into a pooled fixed-size buffer via `Convert.TryFromBase64String` (malformed or
  over-limit → `false`, mapped to 400, never a `FormatException`/500). `POST /api/v1/detections`
  gets `RequestSizeLimitAttribute(8 MiB)`, honoured for minimal APIs by a new
  `ApplyRequestSizeLimitAsync` middleware that reads `IRequestSizeLimitMetadata` →
  `IHttpMaxRequestBodySizeFeature` (this host has no MVC). +6 unit tests (`DetectionEvidenceTests`),
  both guards sabotage-checked. Docs: OPERATIONS.md §3 "Detection evidence".

### MEDIUM

- **15-M1** Geography not enforced on ingest or search. `IngestAsync` checks
  `has_permission(OrgUnit => organizationUnitId)` for non-unscoped callers — **organization only**
  (DetectionRepository.cs:84-92), keyed on `IsUnscopedFor("observation.write")`. `SearchAsync`
  (line 149) uses `authorized_org_units` + `IsUnscopedFor("observation.read")` — org only. No
  `IsUnscopedForGeography`, no geo predicate. 9-H1 family. A worker key scoped to org unit X but
  geo-confined can ingest/search detections for any camera under X regardless of district.
- **15-M2** ✅ PR9 — max 31-day window (400) + `limit` capped at 500. Keyset cursor deliberately NOT added (partitioned time-series; `/events` uses keyset for the same reason — future item if paging becomes needed).
- **15-M2 (orig)** `SearchAsync` has NO maximum time window (endpoint defaults 24h but accepts
  `from=2020&to=now`) and NO keyset pagination — plain `LIMIT`. `detection_event` at ANPR volume
  → the same unbounded-scan risk `/events` is carefully guarded against (7-day hard cap +
  cursor). Add a max window + opaque cursor.
- **15-M3** `ResolveCameraAsync(request.CameraId, ct)` takes no `caller` — resolves any camera in
  the estate by `targetId:nativeId`. Combined with 15-M1 (org-only) an out-of-scope camera id is
  still resolved before the org check; fine functionally but the org check is the only gate and
  it's post-resolve.

### LOW

- **15-L1** Idempotency key is `id` only — a retried POST with the SAME id but DIFFERENT content
  is silently accepted (202) and ignored; the worker believes its correction landed.
- **15-L2** `EventType` stored raw, no vocab validation (contrast camera type). `Confidence`
  stored raw, no 0..1 range check (CLAUDE.md: confidence must be carried and honest).
- **15-L3** `Directory.CreateDirectory(root)` on every ingest; relative `Evidence:RootPath`
  resolves against process CWD — non-deterministic under systemd. Require an absolute path at
  startup.
- **15-L4** Partial-plate search not supported (exact normalized match only).

## Finding 16 — Watchlist (WatchlistEndpoints.cs + WatchlistRepository.cs)

Review 2026-09-04.

### MEDIUM

- **16-M1** `CreateAsync` returns `Task<Created<CreatedResponse>>` — no Problem branch. "One
  active entry per plate per organization" is presumably a unique constraint → `repo.CreateAsync`
  throws on a duplicate → **500** (CameraEndpoints catches `UniqueViolation` → 409; this doesn't).
  Also no visible 403 path — if the repo throws `ForbiddenException` for an out-of-scope
  `OrganizationUnitId` it's an unhandled 500, not 403. Verify repo scope enforcement + add the
  409/403 branches.
- **16-M2** `DeactivateAsync` audit: `before: null, after: null, organizationUnitId: null` — the
  removal audit row records nothing about WHICH plate was taken off the watchlist or under which
  org. "Who removed plate MH12AB1234 and when" is unanswerable. Capture the entry in `before`.
- **16-M3** `AcknowledgeAsync` audit `organizationUnitId: null` + `before: null` — loses the org
  dimension; doesn't record prior ack state. Behaviour of acknowledging an already-acked alert
  (re-ack vs 404) unclear from `AcknowledgeAlertAsync` bool.
- **16-M4** ✅ PR9 — `?page`/`?pageSize` + `acknowledged` (bool) and `plate` filters on `/watchlist/alerts`.
- **16-M4 (orig)** `ListAlertsAsync` — only `limit`. No filter by entry / plate / acknowledged-state /
  time window, no cursor. An operator working the alert queue can't filter to unacknowledged or
  to a plate. Usability + unbounded-ish scan.

### LOW

- **16-L1** Permission taxonomy: `GET /watchlist` (entries) is gated `alert.read`; there is no
  `watchlist.read`. Read = `alert.read`, write = `watchlist.manage`. Functional but odd.
- **16-L2** Watchlist matching at ingest is `FindActiveMatchAsync(organizationUnitId, ...)` — org
  unit only, geography not consulted. **VERIFIED (PR2): `watchlist_entry` has no `site_id` /
  `geographic_area_id` column — there is no geographic dimension on the entity, so org-only is
  correct by design. No change.** (Separately: `WatchlistRepository.DeactivateAsync` has NO scope
  check at all — `WHERE id = @id` — any `watchlist.manage` holder can deactivate any entry
  estate-wide. Tracked for the watchlist PR, not PR2.)
- **16-L3** `CreateWatchlistEntryRequest.Severity` / `Reason` unvalidated (severity vocab? reason
  length/required?).
- **16-L4** `ListAsync` (entries) / `ListAlertsAsync` — verify both AND geography or confirm
  org-only is intended (16-L2).

## Finding 17 — Worker health (WorkerHealthEndpoints.cs + AiWorkerHealthRepository.cs)

Review 2026-09-04. Smallest surface. Deliberately unaudited (high-freq observability, matches
`worker_node`). But the trust model is too loose for a monitoring signal.

### MEDIUM

- **17-M1** **Heartbeat spoofing defeats the signal.** `WorkerHeartbeatRequest.WorkerId` is a
  client-supplied free string; `UpsertHeartbeatAsync` takes NO `CallerContext` and does an
  unaudited upsert. Any `worker.heartbeat` holder can POST `{workerId: "<a real worker>",
  reportedAt: now}` and keep a dead worker looking alive — the doc says "a stale `lastHeartbeatAt`
  is the operational signal that instance has died", and nothing binds a workerId to the key
  reporting it. Also lets an attacker flood fake worker ids to bury a real outage. Fix: bind
  workerId to the caller (derive from / pin to the API key), reject cross-key updates.
- **17-M2** `lastHeartbeatAt` is set from client-supplied `request.ReportedAt`. A future-dated
  value (clock skew or malice) makes a worker look alive indefinitely. Use server `now()` for
  `last_heartbeat_at`; keep the client value as `reported_at` for drift diagnosis only (CLAUDE.md
  #6 — clocks drift).
- **17-M3** `POST /heartbeat` and `GET /` share one permission (`worker.heartbeat`). A worker that
  can report a heartbeat can also enumerate every other worker + hostname — target list for
  17-M1. Split: `worker.read` for the list, `worker.heartbeat` for submit only. Related:
  `worker.heartbeat` is granted to `STATE_ADMIN` (v1.4.sql:55) — a human admin has no reason to
  submit heartbeats (same copy-paste smell as `apikey.manage` on STATE_ADMIN in 8-C1). Admin
  should get the read, not the write.

### LOW

- **17-L1** No unregister / delete / pruning — a decommissioned worker stays forever with an
  ever-staler heartbeat, indistinguishable from a crash. Add a retire endpoint + retention.
- **17-L2** No length/charset validation on `WorkerId` / `Hostname` — over-long → `22001` → 500.
- **17-L3** `ListAsync` unscoped (hostname disclosure) — STILL OPEN (PR9 paginated `/worker-health` but did not scope it). AI
  workers aren't org/geo entities so arguably acceptable — same call as 8-H2.
- **17-L4** No rate limiting on `/heartbeat` — a wedged worker or attacker hammers the upsert.
- Not a finding: not tied to `worker_node` lease registry — deliberate (AI worker owns a static
  partition, not a lease).
