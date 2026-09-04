# Schema Review — federation schema, pending changes for v1.7

Table-by-table review of the `federation` schema (Jaydip, 2026-08-31 → 2026-09-04), plus a
full pass by the postgres-expert agent against PostgreSQL 17. This is the working list of what
gets changed. Nothing here is applied yet.

**Rules that constrain every item below**
- Changes go in a **new version file** (`v1.7.sql` or next). An applied version file is never
  edited. `db/objects/` mirrors are updated in the same commit.
- No PostgreSQL extensions (no PostGIS, no pgcrypto, no citext).
- No runtime schema check — DB change deploys **before** the app binary; there is no rollback
  of a rename without another version file.
- Operator runs all production DDL. Use `lock_timeout`; for any table already holding real data
  use `ADD CONSTRAINT ... NOT VALID` then `VALIDATE CONSTRAINT`.

---

## 0. Do this first — regenerate `db/objects/`

`db/objects/tables/` is materially stale. It is missing `federation_event`'s `_default`
partition, the dedup-index evolution, the v1.3–v1.5 ALTERs (`api_key.revoked_by` etc.), the
`config_audit` / `credential_access_log` default partitions, and the permission/role seed rows.
Several review findings below were chasing ghosts because of it.

**Action:** regenerate the whole `db/objects/` tree from `pg_dump --schema-only` against a
freshly-migrated `trinetra` DB, as its own commit, before the next review pass.

Findings that turned out to be **already handled in `v1.sql`** (stale mirror only, no schema
change):
- `federation_event` `_default` partition — exists (`v1.sql` ~1072), `ensure_event_partitions`
  raises a clear error on the attach-scan hazard, `event_partition_health` monitors it.
- `config_audit_default` (~1197) and `credential_access_log_default` (~1166) — both exist,
  covered by `ensure_audit_partitions`.

---

## 1. High priority — correctness / ops

### 1a. Drop `watchlist_alert_detection_fkey`
`watchlist_alert` FKs `detection_event(occurred_at, event_id)` with `ON DELETE CASCADE`.
Retention drops old `detection_event` daily partitions; `DROP`/`DETACH` of a partition is not a
`DELETE`, so the cascade never fires and the FK silently becomes a false guarantee. It also
couples `watchlist_alert` retention to `detection_event` retention — but alerts are the product
and must be kept longer than raw detections.

- **Drop** `watchlist_alert_detection_fkey`.
- Keep `detection_occurred_at` + `detection_event_id` as plain denormalized columns (needed to
  locate the partition anyway).
- Referential correctness is already guaranteed at write time: the alert is raised in the same
  unit of work as the detection insert (invariant #10).
- Keep `watchlist_alert_watchlist_entry_id_fkey` and `watchlist_alert_acknowledged_by_fkey`
  (small tables, safe). Add `ON DELETE SET NULL` to the `acknowledged_by` FK.
- Add an ops query: periodically check for `watchlist_alert` rows whose
  `(detection_occurred_at, detection_event_id)` no longer resolves, so a code regression
  surfaces.

### 1b. `federation_event` — rename, but NO FK
- `camera_id text` → rename to `native_camera_id`; add `target_id uuid` (nullable, backfill,
  then `NOT NULL` if the model allows). `ALTER ... RENAME COLUMN` on the partitioned parent
  cascades to all partitions, metadata-only.
- Rebuild `ix_event_camera_time` to lead `(target_id, native_camera_id, occurred_at DESC)`.
- **Do NOT add an FK to `federated_camera`.** Same "No FK on purpose" reason as
  `camera_status_history`: an append-only event stream outlives VMS inventory, so an FK would
  reject event inserts for a camera removed from the NVR (data loss on an at-least-once path),
  and adds a per-row probe into `federated_camera` at 80k-camera event volume. Keep
  `(target_id, native_camera_id)` as denormalized columns; document "no FK on purpose — events
  outlive camera inventory."

### 1c. `federation_event.source_event_id` NULL-bypasses dedup (invariant #5)
Postgres treats NULLs as distinct in a unique index, so two events with
`source_event_id IS NULL`, same `source_vms_id`, same `occurred_at` both insert — redelivery
dups. Hits any polling / snapshot-diff adapter with no vendor event id.

- **Preferred:** `ALTER COLUMN source_event_id SET NOT NULL`; adapters synthesize a
  deterministic id for the null case — a hash of (camera + event_type + vendor timestamp +
  object ref).
- **Fallback:** `DROP INDEX ux_event_dedup;` recreate as
  `(source_vms_id, COALESCE(source_event_id, event_id), occurred_at)` — valid on a partitioned
  table. Adapter must still compute `event_id` deterministically for the null case.
- **Reject** `NULLS NOT DISTINCT` on this index — it would collapse all null-vendor-id events
  in the same `(vms, second)` into one row, dropping legitimately distinct events.
- Brief window with no dedup during a `DROP`/recreate swap — do in a maintenance pause or
  prefer the `NOT NULL` route.

### 1d. `occurred_at` must be vendor event time, not ingest time
`occurred_at` is in `ux_event_dedup` / `ux_detection_event_dedup` **only** because a partitioned
unique index must include the partition key. Dedup therefore works only if `occurred_at` is
byte-identical on redelivery. If any adapter derives it from `now()` on receipt, dedup silently
never fires.

- Audit every adapter: `occurred_at` = the vendor's event timestamp.
- Add a comment on both dedup indexes explaining this.

### 1e. `event_id` is not globally unique
PK `(occurred_at, event_id)` makes `event_id` unique only *with* `occurred_at`. Document this so
no downstream consumer builds a lookup or join on `event_id` alone.

---

## 2. Approved DDL changes (registry / RBAC core — low volume, FKs are correct here)

### 2a. `connection_test.requested_by` / `executed_by` split
```sql
-- backfill/verify first: the ::uuid cast fails the whole statement on any non-UUID row
ALTER TABLE federation.connection_test
  ALTER COLUMN requested_by TYPE uuid USING requested_by::uuid;
ALTER TABLE federation.connection_test
  ADD CONSTRAINT connection_test_requested_by_fkey
  FOREIGN KEY (requested_by) REFERENCES federation.platform_users(id);  -- ON DELETE RESTRICT
```
`executed_by` stays `text` nullable (worker identity, like `connector_target.leased_by`).

### 2b. `scopes` — dup-grant unique + `resource_type` CHECK
```sql
CREATE UNIQUE INDEX ux_scopes_grant ON federation.scopes
  (scope_type, organization_unit_id, geographic_area_id, resource_type, resource_id)
  NULLS NOT DISTINCT;

ALTER TABLE federation.scopes
  ADD CONSTRAINT scopes_resource_type_check
  CHECK (resource_type IS NULL OR resource_type IN ('CAMERA','VMS', ...));  -- confirm the set
```
Also: **document the resolution rule** in `docs/AUTHORIZATION.md` and a comment in `scopes.sql`
— absence of an `ORGANIZATION` scope row for a permission means *deny*; "all orgs" is
represented only by `has_unscoped_permission` / `unscoped_permissions`, never inferred from the
geography dimension (invariant #12). No schema change for the rule itself.

### 2c. `geographic_areas.area_type` → FK
```sql
ALTER TABLE federation.geographic_areas
  ADD CONSTRAINT geographic_areas_area_type_fkey
  FOREIGN KEY (area_type) REFERENCES federation.geographic_area_types(code);
```

### 2d. `platform_users.email` — functional unique (citext unavailable)
```sql
CREATE UNIQUE INDEX ux_platform_users_email_lower
  ON federation.platform_users (lower(email))
  WHERE email IS NOT NULL;
-- optional: force normalized storage
ALTER TABLE federation.platform_users
  ADD CONSTRAINT platform_users_email_lower_check CHECK (email = lower(email));
```
Keep `email` nullable (system/service accounts). Reset lookup uses `WHERE lower(email) = lower($1)`.

### 2e. `cameras`
```sql
ALTER TABLE federation.cameras
  ADD CONSTRAINT cameras_vms_id_fkey
  FOREIGN KEY (vms_id) REFERENCES federation.connector_target(id) ON DELETE SET NULL;

ALTER TABLE federation.cameras ALTER COLUMN credential_reference TYPE text;  -- was varchar(255)
```
No FK `cameras.credential_reference → secret` — the secret may be provisioned out-of-band.

### 2f. `organization_units.code` → composite unique
```sql
-- check collisions and global `WHERE code = $1` lookups in the codebase FIRST
SELECT organization_id, code, count(*) FROM federation.organization_units
GROUP BY 1,2 HAVING count(*) > 1;

ALTER TABLE federation.organization_units DROP CONSTRAINT organization_units_code_key;
ALTER TABLE federation.organization_units
  ADD CONSTRAINT uq_organization_units_org_code UNIQUE (organization_id, code);
```
Same shape decision for `sites.code` → `(geographic_area_id, code)` and `geographic_areas.code`
→ `(parent_area_id, code)` (or confirm geo codes are genuinely intended global). All FKs point
at `id`, not `code`, so no cascade breakage. Re-adding a global unique later needs the data to
still be globally unique.

### 2g. `federation_event.severity` CHECK
```sql
ALTER TABLE federation.federation_event
  ADD CONSTRAINT federation_event_severity_check
  CHECK (severity IN ('Info','Low','Medium','High','Critical'));  -- confirm the set
```
CHECK not native enum (enum values can't be reordered/removed; severity is likely to grow).
Validates per-partition — brief `ACCESS EXCLUSIVE` each; use `NOT VALID` + `VALIDATE` if it
holds data.

### 2h. `federation_event_deadletter`
```sql
ALTER TABLE federation.federation_event_deadletter
  ADD CONSTRAINT federation_event_deadletter_target_id_fkey
  FOREIGN KEY (target_id) REFERENCES federation.connector_target(id) ON DELETE CASCADE;
CREATE INDEX ix_deadletter_target_received
  ON federation.federation_event_deadletter (target_id, received_at DESC);
```
Keep it un-partitioned — time-ranged DELETE + autovacuum is fine at deadletter volume.

### 2i. `maintenance_records.performed_by`
Keep `performed_by text` (external contractors have no platform account); **add** optional
`performed_by_user_id uuid REFERENCES platform_users(id)` for when it was a platform user.
Don't force a single FK.

### 2j. `federation_event` vs `detection_event` FK consistency
`detection_event` FKs `organization_units` + `sites`; `federation_event` FKs neither. Pick one
deliberately and apply to both. (Leaning: omit both on the event tables for ingest throughput,
document it — consistent with the "no FK on the ingest path" rule.)

---

## 3. App / ops changes (no schema change)

- **`credential_access_log` → failure-only.** Skip the `AuditAsync(succeeded: true, ...)` call
  on the success path in `PostgresCredentialResolver.ResolveAsync`. Broaden the failure trail to
  any "couldn't connect" reason (network, auth, key-id mismatch, GCM decrypt failure, missing
  secret). Keep `succeeded boolean NOT NULL` in the schema (keeps the partial-index predicate
  valid). Global kill-switch config flag at most — **never** per-target (insider disables it on
  the target they abuse); flag must fail-safe (log if unreadable). Consider dropping
  `ix_credential_access_ref` once only failures are stored.
- **`api_key.last_used_at` throttle.** Conditional update, ~99% fewer writes:
  ```sql
  UPDATE federation.api_key SET last_used_at = now()
  WHERE id = $1 AND (last_used_at IS NULL OR last_used_at < now() - interval '5 minutes');
  ```
  Fire-and-forget; ignore row count. (In-process coalescing in the API host is the alternative
  — hand to dotnet-expert if chosen.)
- **`api_key` expiry** is a query-time filter, not an index predicate (`now()` isn't
  `IMMUTABLE`): `WHERE key_hash = $1 AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at > now())`.
  No index change. (Our "index should exclude expired" finding was wrong.)
- **`access_groups` non-ACTIVE must grant nothing.** Query/function fix in the
  `principal_groups` / `has_permission` chain — a `DRAFT` group with a scope must yield zero
  effective access. Add a regression test and sabotage-check it (flip the filter off, confirm
  the test fails).
- **`maintenance_run` missed-run detection.** Ops query, wire into `docs/OPERATIONS.md`:
  ```sql
  SELECT job, last_run_date FROM federation.maintenance_run WHERE last_run_date < current_date;
  ```
  Same alert protects the `_default` partitions from filling — a missed `ensure_*_partitions`
  run is the failure mode for both. Also **alert hard on `rows_in_default_partition > 0`** —
  treat any row in a default partition as a page.
- **Secret key rotation runbook** → `docs/OPERATIONS.md`. Schema already supports it: per-row
  `key_id` + `ix_secret_key_id`. Lazy re-encryption — introduce new `key_id`, background
  chunked procedure walks `WHERE key_id = '<old>'` decrypt/re-encrypt/`rotated_at`, retire old
  key when `count(*) WHERE key_id = '<old>'` hits zero. No prior-ciphertext history needed for
  rotation (only if value-rollback is a separate requirement — confirm with Jaydip). Optional
  tiny `secret_key` table tracking active/retiring key ids.
- **Confirm `purge_connection_tests_before` (`v1.sql` ~1940) is scheduled.**
- **`password_reset_token` table does not exist** — needed for API review F2 (forgot-password).
  Confirm it's on someone's list.

---

## 4. Comments to add (no behaviour change)

- `config_audit.sql` — `actor_user_id` / `actor_api_key_id` are intentionally unconstrained;
  audit rows outlive their principals (same as `camera_status_history`). And: mutation+audit
  atomicity is enforced app-side via `UnitOfWork` (invariant #10) — no DB trigger; a direct SQL
  mutation bypassing the app produces no audit row.
- `ux_event_dedup` / `ux_detection_event_dedup` — `occurred_at` is in the key for the
  partitioned-unique-index requirement; dedup correctness depends on it being the vendor event
  timestamp, byte-identical on redelivery.
- `detection_event.camera_id` — nullable, no FK: camera may not be in the registry yet (same
  reconciliation pattern as `federated_camera.camera_id`).

---

## 5. Reviewed — no change

- `federation_event` and `detection_event` stay **separate tables** — different producers,
  lifecycles, retention, dedup identity; a discriminator column would force a
  lowest-common-denominator schema.
- `connector_target`, `connector_cursor`, `connector_capability`, `connector_health`,
  `connection_test` (bar 2a) — bitmask `supported` column accepted, no cursor history,
  `connector_health` partitioning fine.
- `geographic_area_types`, `group_scopes`.
- `maintenance_records` lifecycle CHECKs (good), `maintenance_run` as the cron tracker.
- `config_audit` central + app-side `UnitOfWork.AuditAsync` (not per-table trigger shadow
  tables) — deliberate, explained.
- Device credentials write-only over the API — no read path; `/credential/status` returns only
  a boolean.
- `ix_cameras_bbox_live` btree(lat,long) — **adequate** at 80k rows (whole live set is a few
  MB; seq scan is single-digit ms). Our "near-useless" framing was overstated. Revisit only
  with a real `EXPLAIN (ANALYZE, BUFFERS)` on the map-viewport query; PostGIS + GiST is the
  deferred proper fix (gap analysis).
- `config_audit.id` `CACHE 1` — fine at audit volume; bump to 32–64 only if config changes ever
  burst (bulk-import per-row auditing).

---

## 6. One-way doors / risks

- `organization_units` / `sites` / `geographic_areas` `code` unique widening — check collisions
  and global `WHERE code =` lookups first; re-adding a global unique later needs still-unique
  data.
- `connection_test.requested_by` text→uuid — `USING ::uuid` fails the statement on any non-UUID
  row; backfill first.
- `federation_event` column rename — app binary compiled against the old name fails at request
  time; deploy DB then app, no rename rollback without another version file.
- Dropping `ux_event_dedup` to rebuild — brief window with no dedup; prefer `source_event_id
  NOT NULL`.
- Dropping `watchlist_alert_detection_fkey` — now trusting the app unit of work; add the
  dangling-ref ops check.
- Constraint adds take `ACCESS EXCLUSIVE` briefly (per-partition for `severity`); `lock_timeout`
  + `NOT VALID`/`VALIDATE` on populated tables.
