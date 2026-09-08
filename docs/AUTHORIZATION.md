# Authorization — implementation

`RBAC-LOGICAL-FLOW.md` defines the model. This document records how it is enforced in Model 3,
and which of its rules are load-bearing in ways that are easy to break by accident.

---

## 1. Principals

Three kinds of caller reach the data layer, and all three resolve their access the same way.

| Principal | Identity | Acts through |
|---|---|---|
| User | short-lived access JWT from `/api/v1/auth/login` or `/api/v1/auth/refresh` | Their access groups |
| API key | `X-Api-Key`, matched by SHA-256 | The single access group the key names |
| System | None — the CLI and the connector workers | Full rights, audited by component |

**User sessions are revocable.** The access token is a ~15-minute stateless JWT carrying a
`trinetra:tokenver` claim; every authenticated request re-reads `platform_users.token_version`
and rejects a token whose claim no longer matches (or whose user is no longer `ACTIVE`) with a
generic 401. That column is bumped — and all the user's refresh tokens revoked, in the same
transaction — on logout, self password change, admin password reset, deactivation, and removal
from a group. A *grant* (adding a group) is not immediate: it appears on the user's next refresh.
The paired opaque refresh token (8h sliding, hash-only in `refresh_token`, rotated on every use)
is what a client exchanges at `/auth/refresh` for a new pair; re-presenting a rotated one outside
a short grace window is treated as theft and revokes every session.

An **API-key caller** re-resolves its grants from the database on a cache miss, not on every
request: each node holds a presented key's resolved permissions and scope for **up to ~45
seconds**. Revoking a key (`DELETE /api/v1/api-keys/{id}`) evicts that entry immediately on the
node serving the revoke and lets it lapse within ~45s on every other node, so revocation,
expiry, and a change to the key's group (disable, delete, scope edit) all take effect within
~45s fleet-wide rather than on the key's next request. This is a
deliberate trade for removing a two-query round trip from every machine-to-machine request at
the 80,000-camera design point. The key-auth path is also rate limited (20/min per key-hash +
source IP, rejected before any database work) and rejects a key that is not 64 hex chars.

**Login is constant-cost.** Unknown user, wrong password, inactive account and lockout all
return the same 401, and all pay the same full PBKDF2 verify — a missing account is checked
against `PasswordHasher.Decoy` rather than skipping the hash, so response time does not reveal
which usernames exist. A failed attempt against an already-locked account does not extend the
lockout window.

**Signing keys are an ordered ring.** The first key signs, every key validates, and each token
names its key in the `kid` header, so rotation overlaps old and new keys across a restart
(`docs/OPERATIONS.md` §3). The algorithm is pinned (`ValidAlgorithms`), and a token naming a key
not in the ring fails to the same generic 401 as any other bad signature.

**Authentication events are recorded.** `auth_audit` is an append-only trail of logins (success
and — coarsely — failure), lockouts, refresh rotation and replay-theft, logout, self and admin
password changes, on-login rehash, and API-key authentication outcomes, each with source IP and
user-agent. It is read only through `authaudit.read` (SUPER_ADMIN only — deliberately not
bundled with `config.audit.read`), never written through the API, and retained on its own
period (`Retention:AuthAuditMonths`, default 24). The login *response* is coarse — an unknown
username and a wrong password for a real account both return the same 401 — but the *trail*
records which: a resolved `user_id` means the name existed, a `presented_username` with no
`user_id` means it did not. That distinction is for an investigator and is not an enumeration
surface, because reading the trail requires SUPER_ADMIN. API-key *success* is sampled (~one row
per key per five minutes); API-key *failure* is always recorded.

An API key acts **through an access group, exactly as a user does**. This is not a convenience:
two parallel authorization models would inevitably drift, and the weaker one would quietly become
the real policy.

```text
   user ──┐
          ├──> principal_groups(user_id, api_key_id) ──> scopes ──> decision
   key ───┘
```

`principal_groups` takes both identifiers and expects exactly one. If both are supplied, **both
branches are excluded and the result is empty** — an ambiguous principal denies rather than
broadens. A union of the two would hand such a caller the sum of a user's scopes and a key's,
which is the one outcome an authorization function must never produce.

The system principal is not a bypass so much as an accurate description: anything running locally
already holds the database connection string and the credential encryption key, so it can do
everything regardless. Modelling it as restricted would be theatre, and would hide the fact that
terminal access is equivalent to full access.

---

## 2. Two dimensions, and why they cannot be merged

Scope has three types — organization, geography, resource — and they compose by a rule that is
easy to get subtly wrong:

```text
   WITHIN a dimension        OR     Police OR Transport
   ACROSS dimensions         AND    Police AND Ahmedabad
   Unconstrained dimension          unrestricted in that dimension
   Groups                    evaluated INDEPENDENTLY
```

Groups being independent is the one people break. A user is never granted group A's permission
inside group B's scope, which is why the decision function loops per group rather than unioning
permissions and scopes separately.

Each dimension carries its own "unscoped" set, tracked separately end to end:

| Claim | Meaning |
|---|---|
| `trinetra:perm` | Permissions held at all |
| `trinetra:unscoped-perm` | Held through a group declaring **no organization scope** |
| `trinetra:unscoped-geo` | Held through a group declaring **no geographic scope** |

A group may be confined to one department while reaching every district, or the reverse. One
combined flag would let whichever dimension happened to be unconstrained override the other —
either hiding areas a caller may see, or showing areas they may not.

**Unscoped is per permission, never a single flag.** A single flag was a real vulnerability: it
was computed for one permission at login and then honoured as a bypass for every other, so a user
holding an unscoped *read* silently gained unscoped write and administration.

Empty and unscoped are kept explicitly distinct, because an empty scope list is ambiguous — it
means either "reaches nothing" or "reaches everything", and confusing those either locks out the
administrator or exposes the whole estate.

**A row with no site is not geographically constrained.** `connector_target.site_id`,
`detection_event.site_id` and `federation_event.site_id` are all nullable — a VMS target
registered before a site is assigned, or a detection/event from a camera that has not been
reconciled into the registry yet, carries no geographic key. The organization dimension still
applies; the geography dimension cannot, because there is nothing to check it against. A caller
confined to one district therefore still sees every site-less row in their organization,
regardless of district. This is deliberate — it matches the camera registry's own placement rule
(`CameraRepository.RequirePlacementAsync`) rather than failing closed — but it means geographic
confinement is only as complete as the estate's site data. A department rolling out geo-scoped
administrators should assign sites to its targets and reconcile its cameras before relying on
district-level containment.

**The standalone AI worker's key must stay geography-unscoped for `observation.write`**, unless
its access group's geographic scope is deliberately widened to cover every district it processes.
`DetectionRepository.IngestAsync` enforces geography like everything else; a worker key bound to a
geo-restricted group will get `403` on any camera outside that geography, which reads as a silent
partial outage rather than a configuration error. Check this before a multi-district rollout.

---

## 3. Where enforcement happens

Four layers, each covering a failure the others cannot.

```text
   route metadata      .RequirePermission("vms.read")
        |              declares and refuses early; visible in OpenAPI
        v
   handler             caller.Require(...) where the permission varies by branch
        |
        v
   repository          required CallerContext parameter
        |              a query that omits scoping should fail to compile
        v
   SQL                 has_permission() / authorized_org_units()
                       the WHERE clause itself
```

`RBAC-LOGICAL-FLOW.md` §20 is explicit that frontend filtering is not a security boundary. Neither
is a handler that forgets a `WHERE`. So the decision lives in SQL, and every scoped repository
method takes a **required** `CallerContext` — making the context impossible to omit turns
"remembered to scope the query" from a review question into a compile-time one.

Route metadata does not replace the lower layers. A check at the edge cannot scope a `WHERE`
clause, and an endpoint added later without the call would still reach the data layer guarded.
What it adds is a declaration: the permission becomes part of the route's contract instead of a
line buried in a lambda. The API reports coverage at startup, so a route that authenticates and
checks nothing is visible rather than assumed.

**One narrow exception to the compile-time rule.** Users and access groups are not
organization- or geography-scoped *entities* — a user belongs to no unit, a group *has* scopes
rather than sitting under one — so the `CameraRepository`-style `WHERE` predicate does not port
to a fetch of a single one. For a fetch keyed on one principal's own primary key — the
user/group itself (`GET /users/{id}`, `GET /access-groups/{id}`) and its sub-collections
(`/users/{id}/groups`, `/{id}/permissions`, `/access-groups/{id}/members`) — the authority check
runs in the endpoint through one shared, un-bypassable guard (`UserAuthorityGuard`,
`AccessGroupRepository.IsVisibleToAsync`), the same pattern the *write* paths on these entities
already use (`UpdateAsync`, `RevokeMembershipAsync` take no `CallerContext` either). Every
*cross-principal* list — `ListAsync` for users, groups and API keys, and `ListMembersAsync` —
still takes a required `CallerContext` and filters in SQL, so the surface that can leak many
principals' rows at once stays under the compile-time rule.

The list filter and the single-`GET` guard apply the **same** reachability rule for a given
entity, so a row that appears in a list never 404s on its own detail route. For users
specifically, both check organizational reach only (the target holds nothing through an
estate-wide group, and every unit it reaches is one the caller reaches); the extra
"holds no permission the caller lacks" check is a *write*-path escalation guard
(`CanAdministerAsync` condition 1) and does not gate reads. User visibility is organization-only
throughout; groups and keys check both dimensions — see §7.

---

## 4. The SQL surface

| Function | Answers |
|---|---|
| `principal_groups(user, key)` | Which access groups this principal acts through |
| `principal_permissions(user, key)` | What it holds, before scope |
| `has_permission(user, key, perm, org, geo, type, id)` | The full decision for one resource |
| `authorized_org_units(user, key, perm)` | Organization units reachable, as a set |
| `authorized_geographic_areas(user, key, perm)` | Geographic areas reachable, as a set |
| `authorized_organizations(user, key, perm)` | Organizations containing a reachable unit |
| `unscoped_permissions(user, key)` | Permissions held without organization limits |
| `has_unscoped_geography(user, key, perm)` | Whether geography is unconstrained for one permission |

All are `STABLE` and pin `SET search_path = federation, public`. Without that pin a function
resolves its own tables from the *caller's* path — so it works from a psql session that set the
path and fails from the application, which connects with the default.

Hierarchy walking uses recursive descent, so a scope naming "Ahmedabad District" already covers a
camera in a village three levels below. **Scope rows must never enumerate descendants**: a village
added tomorrow would then grant nobody access until every affected row was found and updated, and
nothing would report the omission.

Call sites use **named notation** — `has_permission(p_user_id => …, p_api_key_id => …)` — so a
future parameter change cannot silently rebind positionally.

### Changing these functions

Adding a parameter with `CREATE OR REPLACE` creates an **overload**, not a replacement. Any call
site still passing the old argument list keeps resolving to the old function, with a green build
and passing tests. When a signature changes, `DROP` the old one explicitly so every call site
fails loudly.

---

## 5. Deliberate denials

Some things are refused on purpose, and look like gaps until you know why.

| Refused | Reason |
|---|---|
| An API key administering users | Administering people is a human act. Fail-closed |
| A scoped caller creating an organization | It creates a root no existing scope reaches — territory outside anyone's oversight, including their own administrator's |
| A scoped caller creating a root org unit or root area | Same: it answers to no existing scope |
| Re-parenting into a subtree being deactivated | Detaches it from the root and makes it unreachable to every scope query |
| Widening a group beyond the caller's own reach | Escalation by another route: granting access to a department they cannot themselves see |
| A scoped `role.manage` holder touching a permission they lack (on the new set or the role's existing set) | Same escalation, one step earlier: put the permission in a role, attach a group in your scope, add yourself. Unscoped `role.manage` is exempt. Checked against the `FOR UPDATE` row |
| A **scoped** `role.manage` holder editing, disabling or deleting a **preset** (`is_system`) role | A role is global; a preset change hits every group on it in every department. Preset writes require `role.manage` held unscoped; scoped holders get custom roles only |
| Editing or deleting `SUPER_ADMIN` at all | It is the recovery role the first-start backfill and the platform-admin group depend on. Every other preset is editable (unscoped); presets cannot be deleted, only disabled |
| Activating an access group with no organization *or* no geography scope | An unconstrained dimension is an estate-wide grant. Only an administrator already unscoped for `group.manage` on that dimension may do it |

Out-of-scope reads return **404, not 403**. Distinguishing "does not exist" from "exists but is
not yours" tells an unauthorised caller which ids are real. This holds for the user, access-group
and API-key surfaces too, even though those are not org/geo entities: a scoped administrator sees
only the users they may administer, only the groups they could grant (so a group unrestricted on
a dimension the caller is scoped on — the platform-admin group included — is invisible), and only
the API keys bound to such a group. A single-resource fetch outside that reach is a 404; a list
simply omits the rows; and `DELETE /api/v1/api-keys/{id}` on an unreachable key is a 404 that
changes nothing, so `apikey.manage` can no longer revoke another department's integration key.
The member list of a visible group is filtered the same way the user directory is.

---

## 6. Audit

Every configuration change is recorded with actor, before state and after state. Snapshots rather
than a diff, so the record stays readable years later when the code that produced the diff format
is gone.

**The mutation and its audit row share one transaction.** They were previously written on separate
connections, each committing independently, so a failure between them left the change applied and
unrecorded — and the record that goes missing is by definition the one written closest to whatever
caused the failure.

This is enforced structurally rather than by convention: audit rows are writable **only** through
`UnitOfWork.AuditAsync`, and repository write methods take a required `UnitOfWork`. There is no
path that records a change outside the transaction that made it, and no path that mutates outside
one either.

Credential handling has its own rules:

- Exactly one `GET` returns a stored credential: `GET /api/v1/vms/{id}/credential/resolve`,
  gated on `credential.resolve` (only the `DETECTION_WORKER` machine role holds it), scoped
  through the target, and audited on every call. It exists because Model 2's AI worker connects
  to camera streams directly. If the audit row cannot be written the secret is withheld (503).
  Nothing else — no other route, no other response type — carries credential material.
- The write is audited as *that* it changed, never *to what*.
- The reference comes from the target row, never from the request body.
- `Credential.ToString()` is redacted deliberately.
- Every credential *resolution* is logged separately, in `credential_access_log` — with the
  accessor and the target, success or failure.

---

## 7. Decisions and known gaps

The first is a choice; the two after it are gaps.

- **Row-level security is deliberately not used.** Authorisation is enforced in the application:
  route metadata, a required `CallerContext` on every scoped repository method, and the scope
  predicate in SQL. RLS was considered and rejected — it would need the caller's identity set on
  the connection per request, which breaks under transaction-mode pooling, and it splits one
  authorisation model across two places that can disagree. One model, enforced in one layer, is
  the trade being made. The consequence is that a new query which omits its scope predicate
  returns everything rather than nothing, so the compile-time `CallerContext` requirement is
  load-bearing rather than a convenience — see §3.
- **Token revocation now exists (PR4).** Access tokens were once stateless 8h bearers with no
  revocation path at all — deactivation, group removal and password reset took up to 8h to bite.
  `token_version` + a per-request check closed that; see §1.
- **Auth hardening (PR7).** Login is constant-cost (4-H4) and a locked account's window cannot be
  pushed out (4-L3); `auth_audit` records authentication events (4-H3); the API-key auth path is
  rate limited with a brief grant cache (4-H5 / 8-NEW-H); the HMAC signing key is an ordered ring
  with restart-based rotation (4-H1); `password_history` blocks the last 5 and a 24h minimum age
  (4-M5). Still deferred: **MFA** (4-H2, its own design track), an *asymmetric* signing scheme
  (RS256/JWKS — only worth it once an external party must verify our tokens), breached-password
  screening (4-M6), the must-change-password middleware (4-M1), forwarded-headers / trusted-proxy
  hardening (4-M3 — so a recorded `source_address` is the proxy's IP behind the on-prem proxy),
  and a per-device session list (logout is all-or-nothing).
- **User, access-group and API-key reads were unscoped; now fixed (PR5).** Every `user.read` /
  `group.read` / `apikey.read` holder could list every account, every group (the platform-admin
  roster included) and every service account's privilege level estate-wide, and any
  `apikey.manage` holder could revoke any key. The read repositories took no `CallerContext` at
  all. They now filter by administrative reach — reusing the escalation-guard rule so "what a
  scoped admin can see" equals "what they could grant" — with single-row fetches guarded in the
  endpoint (see §3). One asymmetry remains, tracked for a later pass: user visibility is
  organization-scoped only, matching `CanAdministerAsync`; groups and keys check both dimensions.
  Operational effect once deployed: a scoped administrator will stop seeing rows they saw before
  the change — a wider group or an unscoped read grant is the remedy.
- **Event, detection and VMS-target geographic scoping was incomplete; now fixed.** Until the
  "geography-scope wave" fix, `federation_event`, `detection_event` and `connector_target` were
  scoped on organization only — worse, one read path (`ConnectorTargetRepository.GetAsync`) let
  the *organization* unscoped flag bypass the geography check entirely, so it looked enforced but
  was not. All three now AND both dimensions, each with its own unscoped flag, matching the
  camera registry. See §2 for the one remaining limitation: a row with no site is not
  geographically constrained.
- **The audit `before` snapshot is read outside the transaction.** If another writer changes the
  row in that window, the recorded `before` is stale. Rare, but it is the remaining inaccuracy in
  the trail.
