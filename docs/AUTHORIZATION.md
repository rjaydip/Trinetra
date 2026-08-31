# Authorization — implementation

`RBAC-LOGICAL-FLOW.md` defines the model. This document records how it is enforced in Model 3,
and which of its rules are load-bearing in ways that are easy to break by accident.

---

## 1. Principals

Three kinds of caller reach the data layer, and all three resolve their access the same way.

| Principal | Identity | Acts through |
|---|---|---|
| User | JWT from `/api/v1/auth/login` | Their access groups |
| API key | `X-Api-Key`, matched by SHA-256 | The single access group the key names |
| System | None — the CLI and the connector workers | Full rights, audited by component |

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

Out-of-scope reads return **404, not 403**. Distinguishing "does not exist" from "exists but is
not yours" tells an unauthorised caller which ids are real.

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
- **Event geographic scoping is incomplete.** `federated_camera.camera_id` stays null until Model
  1's registry exists, so events carry organization scope only. Organization scope works fully.
- **The audit `before` snapshot is read outside the transaction.** If another writer changes the
  row in that window, the recorded `before` is stale. Rare, but it is the remaining inaccuracy in
  the trail.
