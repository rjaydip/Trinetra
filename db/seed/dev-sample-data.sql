-- Trinetra — development sample data
--
-- Fills a v1 database with a plausible estate so the frontend has something to render:
-- hierarchies, sites, users across every role, connector targets in every state, ~230
-- federated cameras, a week of health history, ~6,500 normalised events, audit trails.
--
--     psql -U trinetra -d trinetra -f db/versions/v1.sql          # schema first
--     psql -U trinetra -d trinetra -f db/seed/dev-sample-data.sql # then this
--
-- NOT a schema version. It lives outside db/versions/ deliberately: version files are never
-- edited once applied, and this one is expected to change constantly as the UI grows. Nothing
-- in the running system reads it.
--
-- ---------------------------------------------------------------------------------------------
-- DEVELOPMENT AND DEMO ONLY. Never apply this to a production database.
--
--   * Every seeded account shares the password  Trinetra@2026!
--   * Two API keys are seeded with known plaintext values (see §9).
--   * The `secret` rows are PLACEHOLDER ciphertext. They exist so `GET /vms/{id}/credential/
--     status` reports `exists: true`; they will NOT decrypt, so a connector worker that tries to
--     resolve one fails at connect time. That is the intended behaviour here — Model 3 seals
--     credentials application-side with a key the database never holds, so a real credential
--     cannot be seeded from SQL. Write them through `PUT /vms/{id}/credential` if a worker
--     actually needs to connect.
-- ---------------------------------------------------------------------------------------------
--
-- Re-runnable. Fixed UUIDs and ON CONFLICT throughout; the generated volume (cameras, health,
-- events) is anchored to date_trunc('hour', now()), so re-running inside the same hour is a
-- no-op and re-running later adds a fresh window rather than duplicating the old one.
--
-- A teardown block is at the bottom, commented out.

BEGIN;

SET search_path TO federation, public;


-- ===========================================================================
-- 0. partitions
-- ===========================================================================
-- The time-partitioned tables need partitions covering the range this script writes into.
-- Without them every row lands in the DEFAULT partition, which is exactly the condition
-- event_partition_health exists to alert on.
--
-- ensure_event_partitions raises if federation_event_default already holds rows in the range
-- being attached. On a database where the API has been running without partition maintenance,
-- drain that table first.

SELECT ensure_event_partitions((CURRENT_DATE - 8)::date, 16);
SELECT ensure_health_partitions((CURRENT_DATE - 8)::date, 16);
SELECT ensure_audit_partitions((date_trunc('month', CURRENT_DATE) - interval '3 months')::date, 4);


-- ===========================================================================
-- 1. geography
-- ===========================================================================
-- Level registry first, so the UI can order levels and reject a district placed in a village.

INSERT INTO geographic_area_types (code, name, level_order, status) VALUES
    ('STATE',    'State',    1, 'ACTIVE'),
    ('DISTRICT', 'District', 2, 'ACTIVE'),
    ('ZONE',     'Zone',     3, 'ACTIVE'),
    ('WARD',     'Ward',     4, 'ACTIVE')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, level_order = EXCLUDED.level_order, status = EXCLUDED.status;

-- Inserted parent-first: the acyclic trigger reads the parent row on INSERT.
INSERT INTO geographic_areas (id, parent_area_id, code, name, area_type, status) VALUES
    ('a0000000-0000-4000-8000-000000000001', NULL,
     'GJ', 'Gujarat', 'STATE', 'ACTIVE'),

    ('a0000000-0000-4000-8000-000000000002', 'a0000000-0000-4000-8000-000000000001',
     'GJ-AHM', 'Ahmedabad', 'DISTRICT', 'ACTIVE'),
    ('a0000000-0000-4000-8000-000000000003', 'a0000000-0000-4000-8000-000000000001',
     'GJ-SRT', 'Surat', 'DISTRICT', 'ACTIVE'),
    ('a0000000-0000-4000-8000-000000000004', 'a0000000-0000-4000-8000-000000000001',
     'GJ-VAD', 'Vadodara', 'DISTRICT', 'ACTIVE'),
    -- Inactive on purpose: the list endpoints filter on status, and nothing exercises that
    -- filter unless at least one row fails it.
    ('a0000000-0000-4000-8000-000000000012', 'a0000000-0000-4000-8000-000000000001',
     'GJ-RJT', 'Rajkot', 'DISTRICT', 'INACTIVE'),

    ('a0000000-0000-4000-8000-000000000005', 'a0000000-0000-4000-8000-000000000002',
     'AHM-Z-EAST', 'Ahmedabad East Zone', 'ZONE', 'ACTIVE'),
    ('a0000000-0000-4000-8000-000000000006', 'a0000000-0000-4000-8000-000000000002',
     'AHM-Z-WEST', 'Ahmedabad West Zone', 'ZONE', 'ACTIVE'),
    ('a0000000-0000-4000-8000-000000000007', 'a0000000-0000-4000-8000-000000000003',
     'SRT-Z-CENTRAL', 'Surat Central Zone', 'ZONE', 'ACTIVE'),
    ('a0000000-0000-4000-8000-000000000008', 'a0000000-0000-4000-8000-000000000004',
     'VAD-Z-CITY', 'Vadodara City Zone', 'ZONE', 'ACTIVE'),

    ('a0000000-0000-4000-8000-000000000009', 'a0000000-0000-4000-8000-000000000005',
     'AHM-W-MANINAGAR', 'Maninagar Ward', 'WARD', 'ACTIVE'),
    ('a0000000-0000-4000-8000-000000000010', 'a0000000-0000-4000-8000-000000000006',
     'AHM-W-NARANPURA', 'Naranpura Ward', 'WARD', 'ACTIVE'),
    ('a0000000-0000-4000-8000-000000000011', 'a0000000-0000-4000-8000-000000000007',
     'SRT-W-ADAJAN', 'Adajan Ward', 'WARD', 'ACTIVE')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, area_type = EXCLUDED.area_type, status = EXCLUDED.status;

INSERT INTO sites (id, code, name, geographic_area_id, site_type, address, latitude, longitude, status) VALUES
    ('d0000000-0000-4000-8000-000000000001', 'SITE-AHM-SG-JN', 'S.G. Highway Junction',
     'a0000000-0000-4000-8000-000000000010', 'JUNCTION',
     'S.G. Highway at Drive-In Road, Naranpura, Ahmedabad', 23.0388000, 72.5320000, 'ACTIVE'),
    ('d0000000-0000-4000-8000-000000000002', 'SITE-AHM-MANI-SQ', 'Maninagar Square',
     'a0000000-0000-4000-8000-000000000009', 'JUNCTION',
     'Maninagar Cross Roads, Ahmedabad', 22.9967000, 72.6020000, 'ACTIVE'),
    ('d0000000-0000-4000-8000-000000000003', 'SITE-AHM-CTRL', 'Ahmedabad City Control Room',
     'a0000000-0000-4000-8000-000000000006', 'CONTROL_ROOM',
     'Police Commissionerate, Shahibaug, Ahmedabad', 23.0225000, 72.5714000, 'ACTIVE'),
    ('d0000000-0000-4000-8000-000000000004', 'SITE-AHM-KANKARIA', 'Kankaria Lakefront',
     'a0000000-0000-4000-8000-000000000009', 'PUBLIC_SPACE',
     'Kankaria Lake, Maninagar, Ahmedabad', 22.9930000, 72.6020000, 'ACTIVE'),
    ('d0000000-0000-4000-8000-000000000005', 'SITE-SRT-RING', 'Ring Road Corridor',
     'a0000000-0000-4000-8000-000000000011', 'CORRIDOR',
     'Ring Road, Adajan, Surat', 21.1900000, 72.8180000, 'ACTIVE'),
    ('d0000000-0000-4000-8000-000000000006', 'SITE-SRT-CTRL', 'Surat City Control Room',
     'a0000000-0000-4000-8000-000000000007', 'CONTROL_ROOM',
     'Police Commissionerate, Athwalines, Surat', 21.1702000, 72.8311000, 'ACTIVE'),
    ('d0000000-0000-4000-8000-000000000007', 'SITE-VAD-ALKAPURI', 'Alkapuri Circle',
     'a0000000-0000-4000-8000-000000000008', 'JUNCTION',
     'Alkapuri, Vadodara', 22.3110000, 73.1750000, 'ACTIVE'),
    ('d0000000-0000-4000-8000-000000000008', 'SITE-VAD-STN', 'Vadodara Railway Station',
     'a0000000-0000-4000-8000-000000000008', 'TRANSIT_HUB',
     'Sayajigunj, Vadodara', 22.3105000, 73.1810000, 'ACTIVE'),
    ('d0000000-0000-4000-8000-000000000009', 'SITE-AMC-DEPOT', 'AMC Central Depot',
     'a0000000-0000-4000-8000-000000000006', 'DEPOT',
     'Municipal Depot, Naranpura, Ahmedabad', 23.0470000, 72.5600000, 'ACTIVE'),
    ('d0000000-0000-4000-8000-000000000010', 'SITE-HQ-LAB', 'State HQ Integration Lab',
     'a0000000-0000-4000-8000-000000000006', 'LAB',
     'State Police Headquarters, Gandhinagar Road, Ahmedabad', 23.0300000, 72.5800000, 'ACTIVE')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, geographic_area_id = EXCLUDED.geographic_area_id,
        site_type = EXCLUDED.site_type, address = EXCLUDED.address,
        latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude;


-- ===========================================================================
-- 2. organizations
-- ===========================================================================
-- The second, independent dimension. A camera has an owner here and a place above; the two
-- trees are never merged.

INSERT INTO organizations (id, code, name, organization_type, description, status) VALUES
    ('b0000000-0000-4000-8000-000000000001', 'ORG-GP', 'Gujarat Police',
     'LAW_ENFORCEMENT', 'State police organization', 'ACTIVE'),
    ('b0000000-0000-4000-8000-000000000002', 'ORG-MUN', 'Municipal Corporations',
     'MUNICIPAL', 'City municipal corporations operating civic camera estates', 'ACTIVE')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, organization_type = EXCLUDED.organization_type,
        description = EXCLUDED.description, status = EXCLUDED.status;

-- Parent-first again: assert_org_unit_acyclic reads the parent to check it is in the same
-- organization.
INSERT INTO organization_units (id, organization_id, parent_unit_id, code, name, unit_type, status) VALUES
    ('c0000000-0000-4000-8000-000000000001', 'b0000000-0000-4000-8000-000000000001', NULL,
     'GP-HQ', 'Gujarat Police Headquarters', 'STATE_HQ', 'ACTIVE'),
    ('c0000000-0000-4000-8000-000000000008', 'b0000000-0000-4000-8000-000000000002', NULL,
     'MUN-HQ', 'Municipal Corporations Headquarters', 'HQ', 'ACTIVE'),

    ('c0000000-0000-4000-8000-000000000002', 'b0000000-0000-4000-8000-000000000001',
     'c0000000-0000-4000-8000-000000000001',
     'GP-AHM', 'Ahmedabad City Police', 'COMMISSIONERATE', 'ACTIVE'),
    ('c0000000-0000-4000-8000-000000000005', 'b0000000-0000-4000-8000-000000000001',
     'c0000000-0000-4000-8000-000000000001',
     'GP-SRT', 'Surat City Police', 'COMMISSIONERATE', 'ACTIVE'),
    ('c0000000-0000-4000-8000-000000000007', 'b0000000-0000-4000-8000-000000000001',
     'c0000000-0000-4000-8000-000000000001',
     'GP-VAD', 'Vadodara City Police', 'COMMISSIONERATE', 'ACTIVE'),
    ('c0000000-0000-4000-8000-000000000011', 'b0000000-0000-4000-8000-000000000001',
     'c0000000-0000-4000-8000-000000000001',
     'GP-RJT', 'Rajkot City Police', 'COMMISSIONERATE', 'INACTIVE'),

    ('c0000000-0000-4000-8000-000000000003', 'b0000000-0000-4000-8000-000000000001',
     'c0000000-0000-4000-8000-000000000002',
     'GP-AHM-TRF', 'Ahmedabad Traffic Branch', 'BRANCH', 'ACTIVE'),
    ('c0000000-0000-4000-8000-000000000004', 'b0000000-0000-4000-8000-000000000001',
     'c0000000-0000-4000-8000-000000000002',
     'GP-AHM-CR', 'Ahmedabad Crime Branch', 'BRANCH', 'ACTIVE'),
    ('c0000000-0000-4000-8000-000000000006', 'b0000000-0000-4000-8000-000000000001',
     'c0000000-0000-4000-8000-000000000005',
     'GP-SRT-TRF', 'Surat Traffic Branch', 'BRANCH', 'ACTIVE'),

    ('c0000000-0000-4000-8000-000000000009', 'b0000000-0000-4000-8000-000000000002',
     'c0000000-0000-4000-8000-000000000008',
     'MUN-AMC', 'Ahmedabad Municipal Corporation', 'CORPORATION', 'ACTIVE'),
    ('c0000000-0000-4000-8000-000000000010', 'b0000000-0000-4000-8000-000000000002',
     'c0000000-0000-4000-8000-000000000008',
     'MUN-SMC', 'Surat Municipal Corporation', 'CORPORATION', 'ACTIVE')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, unit_type = EXCLUDED.unit_type, status = EXCLUDED.status;


-- ===========================================================================
-- 3. users
-- ===========================================================================
-- Every account below has the password:
--
--     Trinetra@2026!
--
-- PBKDF2-HMAC-SHA256, 600,000 iterations — the current PasswordHasher.DefaultIterations, so
-- these verify without triggering the rehash-on-login path. Salts are derived deterministically
-- from the username so this file is reproducible; that is acceptable precisely because these are
-- throwaway demo accounts and unacceptable anywhere else.
--
-- must_change_password is FALSE so a login lands straight in the app. The real bootstrap admin,
-- seeded by AdminSeeder at API startup, is untouched by this file.

INSERT INTO platform_users (
    id, username, display_name, email,
    password_hash, password_salt, password_iterations, password_algorithm,
    must_change_password, status, failed_login_count, locked_until, last_login_at, is_system)
VALUES
    ('e0000000-0000-4000-8000-000000000001', 'state.admin', 'Meera Joshi',
     'meera.joshi@demo.trinetra.local',
     '\x0fb0380335ad3d4fbae25740d882bad8012fc274eeffd4ebd0df43998ffade66',
     '\xd7867a1b26f0d0d58e5adf7a969ab3fa', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '2 hours', FALSE),

    ('e0000000-0000-4000-8000-000000000002', 'vms.admin', 'Rakesh Patel',
     'rakesh.patel@demo.trinetra.local',
     '\x00bcb37f36f6075162e3c8d544d8bf73e1be7db99a5b777979069d28e7b96e45',
     '\x12c61f78bd6130223c15bb2a94e512e9', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '35 minutes', FALSE),

    ('e0000000-0000-4000-8000-000000000003', 'ahmedabad.admin', 'Nilesh Shah',
     'nilesh.shah@demo.trinetra.local',
     '\x15a81b9d49f5ae9eb0f17597cf06448c14f6915ea98d2b009b0a972ee9cb4930',
     '\x94ce2b2ea20c6d7799dad97466a6ca0f', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '1 day', FALSE),

    ('e0000000-0000-4000-8000-000000000004', 'surat.operator', 'Kavita Desai',
     'kavita.desai@demo.trinetra.local',
     '\xa1682a3bc234f1297f71893c27379db5cea47325e5da51b08d8d4970b9c1efd0',
     '\x589b8b0a6db8677380aa46bdea413177', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '4 hours', FALSE),

    ('e0000000-0000-4000-8000-000000000005', 'traffic.operator', 'Imran Qureshi',
     'imran.qureshi@demo.trinetra.local',
     '\x204c69ada9a408c4ca0875842f55e46253003c975e040861ccb2aed248c6b9da',
     '\xe277c83b8ff6fb0decc3679124c79baa', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '20 minutes', FALSE),

    ('e0000000-0000-4000-8000-000000000006', 'investigator', 'Priya Nair',
     'priya.nair@demo.trinetra.local',
     '\x6e30eec8fc6981ba04641c4444a7f75c3c4f69147b740b9dba7873862ee956fc',
     '\xdb8e24aaee9ff1e13ba75cf12ec877d2', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '3 days', FALSE),

    ('e0000000-0000-4000-8000-000000000007', 'maintenance.tech', 'Sanjay Bhatt',
     'sanjay.bhatt@demo.trinetra.local',
     '\x17b4e6894545db2e07152651e550b3ba0a209cab112b88fd15207e2e8b924ed5',
     '\x4ce9aa93e348de2757865bf5d1c66d86', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '6 hours', FALSE),

    ('e0000000-0000-4000-8000-000000000008', 'analyst', 'Farida Vohra',
     'farida.vohra@demo.trinetra.local',
     '\x30f0387d63810c356ab1a0acf859861576f78df2ba4e069ad958e5782dc2aa0e',
     '\xae89d542d9c1416da2b50a34f372cc30', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '9 hours', FALSE),

    ('e0000000-0000-4000-8000-000000000009', 'viewer.lab', 'Dinesh Rana',
     'dinesh.rana@demo.trinetra.local',
     '\x83ce94118d1386a880bf0c0088cda5380cd470d7be78867bf6992ac202743940',
     '\x4f19461d021eadc3f116dcd7d6a95544', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '2 days', FALSE),

    -- Locked out. The UI has to render this state, and nothing produces it on demand.
    ('e0000000-0000-4000-8000-000000000010', 'locked.account', 'Bhavesh Solanki',
     'bhavesh.solanki@demo.trinetra.local',
     '\x7860c38b845f86bfad94fd72a1128348665336ca74cfe48b14b702910a2d2f12',
     '\x8c2ba16da26877c37d14a81f8a81da16', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'LOCKED', 5, now() + interval '25 minutes', now() - interval '1 hour', FALSE),

    -- Active user whose group membership has lapsed: holds no permissions at all.
    ('e0000000-0000-4000-8000-000000000011', 'expired.member', 'Anita Chauhan',
     'anita.chauhan@demo.trinetra.local',
     '\xcf94d463668beb904da778d67db7f6d61d72b227f2773d6f246b0e4594143157',
     '\xe07cb1fcd131074234ab3302c6c77a38', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'ACTIVE', 0, NULL, now() - interval '20 days', FALSE),

    -- Member of a DRAFT group. Also holds nothing — a different reason, same symptom.
    ('e0000000-0000-4000-8000-000000000012', 'pending.user', 'Hardik Vyas',
     'hardik.vyas@demo.trinetra.local',
     '\x8ebd01fffca16f9d40f022640c1a00842220bf6a46a929981928379aaac39132',
     '\x183d97d5265e2e3a0196a3c7070aa6ff', 600000, 'PBKDF2-HMAC-SHA256',
     TRUE, 'ACTIVE', 0, NULL, NULL, FALSE),

    -- Deactivated. user_effective_access excludes it entirely.
    ('e0000000-0000-4000-8000-000000000013', 'retired.operator', 'Jignesh Parmar',
     'jignesh.parmar@demo.trinetra.local',
     '\x7860c38b845f86bfad94fd72a1128348665336ca74cfe48b14b702910a2d2f12',
     '\x8c2ba16da26877c37d14a81f8a81da16', 600000, 'PBKDF2-HMAC-SHA256',
     FALSE, 'INACTIVE', 0, NULL, now() - interval '120 days', FALSE)
ON CONFLICT (username) DO UPDATE
    SET display_name = EXCLUDED.display_name, email = EXCLUDED.email,
        password_hash = EXCLUDED.password_hash, password_salt = EXCLUDED.password_salt,
        password_iterations = EXCLUDED.password_iterations,
        must_change_password = EXCLUDED.must_change_password,
        status = EXCLUDED.status, locked_until = EXCLUDED.locked_until;


-- ===========================================================================
-- 4. scopes
-- ===========================================================================
-- Each row names exactly ONE point in ONE dimension. Descendants follow from the hierarchy
-- functions and are never enumerated here.

INSERT INTO scopes (id, scope_type, organization_unit_id, geographic_area_id,
                    resource_type, resource_id, description) VALUES
    -- Organization
    ('f0000000-0000-4000-8000-000000000001', 'ORGANIZATION',
     'c0000000-0000-4000-8000-000000000002', NULL, NULL, NULL,
     'Ahmedabad City Police and everything beneath it'),
    ('f0000000-0000-4000-8000-000000000002', 'ORGANIZATION',
     'c0000000-0000-4000-8000-000000000005', NULL, NULL, NULL,
     'Surat City Police and everything beneath it'),
    ('f0000000-0000-4000-8000-000000000003', 'ORGANIZATION',
     'c0000000-0000-4000-8000-000000000003', NULL, NULL, NULL,
     'Ahmedabad Traffic Branch only'),
    ('f0000000-0000-4000-8000-000000000004', 'ORGANIZATION',
     'c0000000-0000-4000-8000-000000000009', NULL, NULL, NULL,
     'Ahmedabad Municipal Corporation'),
    ('f0000000-0000-4000-8000-000000000005', 'ORGANIZATION',
     'c0000000-0000-4000-8000-000000000007', NULL, NULL, NULL,
     'Vadodara City Police'),
    ('f0000000-0000-4000-8000-000000000006', 'ORGANIZATION',
     'c0000000-0000-4000-8000-000000000010', NULL, NULL, NULL,
     'Surat Municipal Corporation'),

    -- Geography
    ('f0000000-0000-4000-8000-000000000010', 'GEOGRAPHY',
     NULL, 'a0000000-0000-4000-8000-000000000002', NULL, NULL,
     'Ahmedabad district and everything beneath it'),
    ('f0000000-0000-4000-8000-000000000011', 'GEOGRAPHY',
     NULL, 'a0000000-0000-4000-8000-000000000003', NULL, NULL,
     'Surat district and everything beneath it'),
    ('f0000000-0000-4000-8000-000000000012', 'GEOGRAPHY',
     NULL, 'a0000000-0000-4000-8000-000000000005', NULL, NULL,
     'Ahmedabad East Zone'),

    -- Resource pinning: one target, nothing else.
    ('f0000000-0000-4000-8000-000000000020', 'RESOURCE',
     NULL, NULL, 'connector_target', 'c1000000-0000-4000-8000-000000000012',
     'The HQ integration lab simulator only')
ON CONFLICT (id) DO NOTHING;


-- ===========================================================================
-- 5. access groups and membership
-- ===========================================================================
-- Role answers "what can they do"; scopes answer "where". A group with NO organization scope is
-- unrestricted in that dimension — which is what makes GRP-STATE-ADMINS and GRP-ANALYSTS
-- different from the rest, and worth having both shapes present.

INSERT INTO access_groups (id, code, name, description, role_id, status, created_by) VALUES
    ('a1000000-0000-4000-8000-000000000001', 'GRP-STATE-ADMINS', 'State Administrators',
     'Administration across every organization. No scope rows, so unrestricted in both dimensions.',
     (SELECT id FROM roles WHERE code = 'STATE_ADMIN'), 'ACTIVE',
     'e0000000-0000-4000-8000-000000000001'),

    ('a1000000-0000-4000-8000-000000000002', 'GRP-VMS-ADMINS', 'VMS Administrators',
     'Connector and adapter administration, including credential writing.',
     (SELECT id FROM roles WHERE code = 'VMS_ADMIN'), 'ACTIVE',
     'e0000000-0000-4000-8000-000000000001'),

    ('a1000000-0000-4000-8000-000000000003', 'GRP-AHM-ADMINS', 'Ahmedabad Administrators',
     'Department administration confined to Ahmedabad City Police AND the Ahmedabad district.',
     (SELECT id FROM roles WHERE code = 'DEPARTMENT_ADMIN'), 'ACTIVE',
     'e0000000-0000-4000-8000-000000000001'),

    ('a1000000-0000-4000-8000-000000000004', 'GRP-SRT-OPS', 'Surat VMS Operators',
     'Operate Surat connector targets without configuring them.',
     (SELECT id FROM roles WHERE code = 'VMS_OPERATOR'), 'ACTIVE',
     'e0000000-0000-4000-8000-000000000001'),

    ('a1000000-0000-4000-8000-000000000005', 'GRP-AHM-TRAFFIC-OPS', 'Ahmedabad Traffic Operators',
     'Camera operation for the traffic branch, East Zone.',
     (SELECT id FROM roles WHERE code = 'CAMERA_OPERATOR'), 'ACTIVE',
     'e0000000-0000-4000-8000-000000000003'),

    ('a1000000-0000-4000-8000-000000000006', 'GRP-INVESTIGATIONS', 'Investigations Team',
     'Search and playback across Ahmedabad and Surat. Geography-scoped, organization-unrestricted.',
     (SELECT id FROM roles WHERE code = 'INVESTIGATOR'), 'ACTIVE',
     'e0000000-0000-4000-8000-000000000001'),

    ('a1000000-0000-4000-8000-000000000007', 'GRP-AMC-MAINT', 'AMC Maintenance',
     'Camera health and maintenance for the Ahmedabad Municipal Corporation estate.',
     (SELECT id FROM roles WHERE code = 'MAINTENANCE_OPERATOR'), 'ACTIVE',
     'e0000000-0000-4000-8000-000000000003'),

    ('a1000000-0000-4000-8000-000000000008', 'GRP-ANALYSTS', 'Analysts',
     'Read-only analytics across the whole estate.',
     (SELECT id FROM roles WHERE code = 'ANALYST'), 'ACTIVE',
     'e0000000-0000-4000-8000-000000000001'),

    ('a1000000-0000-4000-8000-000000000009', 'GRP-LAB-VIEWERS', 'Integration Lab Viewers',
     'Pinned to one connector target by a RESOURCE scope.',
     (SELECT id FROM roles WHERE code = 'VIEWER'), 'ACTIVE',
     'e0000000-0000-4000-8000-000000000002'),

    -- DRAFT grants nothing. Assembled and awaiting review.
    ('a1000000-0000-4000-8000-000000000010', 'GRP-VAD-OPS-DRAFT', 'Vadodara Operators (draft)',
     'Being assembled for the Vadodara rollout. DRAFT, so it grants nothing yet.',
     (SELECT id FROM roles WHERE code = 'CAMERA_OPERATOR'), 'DRAFT',
     'e0000000-0000-4000-8000-000000000001'),

    ('a1000000-0000-4000-8000-000000000011', 'GRP-SMC-OPS-RETIRED', 'Surat Municipal Operators (retired)',
     'Superseded when the SMC estate moved onto the shared connector fleet.',
     (SELECT id FROM roles WHERE code = 'VMS_OPERATOR'), 'DISABLED',
     'e0000000-0000-4000-8000-000000000001')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, description = EXCLUDED.description,
        role_id = EXCLUDED.role_id, status = EXCLUDED.status;

INSERT INTO group_scopes (group_id, scope_id) VALUES
    -- GRP-STATE-ADMINS and GRP-VMS-ADMINS deliberately have none.
    ('a1000000-0000-4000-8000-000000000003', 'f0000000-0000-4000-8000-000000000001'),
    ('a1000000-0000-4000-8000-000000000003', 'f0000000-0000-4000-8000-000000000010'),
    ('a1000000-0000-4000-8000-000000000004', 'f0000000-0000-4000-8000-000000000002'),
    ('a1000000-0000-4000-8000-000000000005', 'f0000000-0000-4000-8000-000000000003'),
    ('a1000000-0000-4000-8000-000000000005', 'f0000000-0000-4000-8000-000000000012'),
    -- Two GEOGRAPHY scopes on one group: within a dimension they OR.
    ('a1000000-0000-4000-8000-000000000006', 'f0000000-0000-4000-8000-000000000010'),
    ('a1000000-0000-4000-8000-000000000006', 'f0000000-0000-4000-8000-000000000011'),
    ('a1000000-0000-4000-8000-000000000007', 'f0000000-0000-4000-8000-000000000004'),
    ('a1000000-0000-4000-8000-000000000009', 'f0000000-0000-4000-8000-000000000020'),
    ('a1000000-0000-4000-8000-000000000010', 'f0000000-0000-4000-8000-000000000005'),
    ('a1000000-0000-4000-8000-000000000011', 'f0000000-0000-4000-8000-000000000006')
ON CONFLICT DO NOTHING;

INSERT INTO user_groups (id, user_id, group_id, assigned_by, expires_at, status) VALUES
    ('a4000000-0000-4000-8000-000000000001', 'e0000000-0000-4000-8000-000000000001',
     'a1000000-0000-4000-8000-000000000001', NULL, NULL, 'ACTIVE'),
    ('a4000000-0000-4000-8000-000000000002', 'e0000000-0000-4000-8000-000000000002',
     'a1000000-0000-4000-8000-000000000002', 'e0000000-0000-4000-8000-000000000001', NULL, 'ACTIVE'),
    ('a4000000-0000-4000-8000-000000000003', 'e0000000-0000-4000-8000-000000000003',
     'a1000000-0000-4000-8000-000000000003', 'e0000000-0000-4000-8000-000000000001', NULL, 'ACTIVE'),
    ('a4000000-0000-4000-8000-000000000004', 'e0000000-0000-4000-8000-000000000004',
     'a1000000-0000-4000-8000-000000000004', 'e0000000-0000-4000-8000-000000000001', NULL, 'ACTIVE'),
    ('a4000000-0000-4000-8000-000000000005', 'e0000000-0000-4000-8000-000000000005',
     'a1000000-0000-4000-8000-000000000005', 'e0000000-0000-4000-8000-000000000003', NULL, 'ACTIVE'),
    ('a4000000-0000-4000-8000-000000000006', 'e0000000-0000-4000-8000-000000000006',
     'a1000000-0000-4000-8000-000000000006', 'e0000000-0000-4000-8000-000000000001', NULL, 'ACTIVE'),
    ('a4000000-0000-4000-8000-000000000007', 'e0000000-0000-4000-8000-000000000007',
     'a1000000-0000-4000-8000-000000000007', 'e0000000-0000-4000-8000-000000000003', NULL, 'ACTIVE'),
    ('a4000000-0000-4000-8000-000000000008', 'e0000000-0000-4000-8000-000000000008',
     'a1000000-0000-4000-8000-000000000008', 'e0000000-0000-4000-8000-000000000001', NULL, 'ACTIVE'),
    ('a4000000-0000-4000-8000-000000000009', 'e0000000-0000-4000-8000-000000000009',
     'a1000000-0000-4000-8000-000000000009', 'e0000000-0000-4000-8000-000000000002', NULL, 'ACTIVE'),

    -- A second group for one user: permissions union, but each stays bound to its own group's
    -- scopes. Worth having, because that rule is the easiest one to break in a UI.
    ('a4000000-0000-4000-8000-000000000010', 'e0000000-0000-4000-8000-000000000005',
     'a1000000-0000-4000-8000-000000000006', 'e0000000-0000-4000-8000-000000000001',
     now() + interval '21 days', 'ACTIVE'),

    -- Lapsed on its own, as temporary investigation access is meant to.
    ('a4000000-0000-4000-8000-000000000011', 'e0000000-0000-4000-8000-000000000011',
     'a1000000-0000-4000-8000-000000000006', 'e0000000-0000-4000-8000-000000000001',
     now() - interval '1 day', 'ACTIVE'),

    ('a4000000-0000-4000-8000-000000000012', 'e0000000-0000-4000-8000-000000000012',
     'a1000000-0000-4000-8000-000000000010', 'e0000000-0000-4000-8000-000000000001', NULL, 'ACTIVE'),

    ('a4000000-0000-4000-8000-000000000013', 'e0000000-0000-4000-8000-000000000010',
     'a1000000-0000-4000-8000-000000000004', 'e0000000-0000-4000-8000-000000000001', NULL, 'ACTIVE'),

    ('a4000000-0000-4000-8000-000000000014', 'e0000000-0000-4000-8000-000000000013',
     'a1000000-0000-4000-8000-000000000011', 'e0000000-0000-4000-8000-000000000001', NULL, 'REVOKED')
ON CONFLICT (user_id, group_id) DO UPDATE
    SET expires_at = EXCLUDED.expires_at, status = EXCLUDED.status;


-- ===========================================================================
-- 6. connector targets
-- ===========================================================================
-- The unit of scale, scheduling and failover. Every target_state is represented, and the lease
-- columns cover claimed, unclaimed, and expired-because-the-worker-died.
--
-- Rate limits differ per target on purpose: vendor tolerance varies by an order of magnitude,
-- and a UI that assumes one global value will look wrong the first time it meets a real estate.

INSERT INTO connector_target (
    id, code, organization_unit_id, site_id, display_name, vendor, runtime_class,
    endpoint, credential_reference, verify_tls, state,
    rate_limit_per_second, rate_limit_burst, inventory_poll_seconds, status_poll_seconds,
    event_poll_seconds, max_concurrent_requests, expected_camera_count,
    leased_by, lease_expires_at, last_claimed_at, created_by)
VALUES
    ('c1000000-0000-4000-8000-000000000001', 'VMS-AHM-TRF-01',
     'c0000000-0000-4000-8000-000000000003', 'd0000000-0000-4000-8000-000000000001',
     'S.G. Highway ONVIF NVR', 'Onvif', 'Managed',
     'https://192.0.2.11:8443/onvif/device_service', 'cred/vms/ahm-trf-01', TRUE, 'Active',
     5.0, 10, 300, 30, 10, 4, 24,
     'worker-ahm-01', now() + interval '40 seconds', now() - interval '3 days',
     'e0000000-0000-4000-8000-000000000002'),

    ('c1000000-0000-4000-8000-000000000002', 'VMS-AHM-TRF-02',
     'c0000000-0000-4000-8000-000000000003', 'd0000000-0000-4000-8000-000000000002',
     'Maninagar Square Hikvision NVR', 'HikvisionIsapi', 'Managed',
     'https://192.0.2.12/ISAPI', 'cred/vms/ahm-trf-02', FALSE, 'Active',
     8.0, 16, 300, 30, 5, 6, 18,
     'worker-ahm-01', now() + interval '38 seconds', now() - interval '3 days',
     'e0000000-0000-4000-8000-000000000002'),

    ('c1000000-0000-4000-8000-000000000003', 'VMS-AHM-CR-01',
     'c0000000-0000-4000-8000-000000000004', 'd0000000-0000-4000-8000-000000000003',
     'Crime Branch Milestone XProtect', 'MilestoneGateway', 'Native',
     'https://192.0.2.20/ManagementServer', 'cred/vms/ahm-cr-01', TRUE, 'Active',
     3.0, 6, 600, 60, 10, 2, 32,
     'worker-ahm-02', now() + interval '45 seconds', now() - interval '9 days',
     'e0000000-0000-4000-8000-000000000002'),

    -- Quarantined: the runtime took it out of rotation. It has no secret row (see §8), which is
    -- the ordinary cause of the AuthFailed health below.
    ('c1000000-0000-4000-8000-000000000004', 'VMS-AHM-CR-02',
     'c0000000-0000-4000-8000-000000000004', 'd0000000-0000-4000-8000-000000000004',
     'Kankaria Dahua NVR', 'DahuaCgi', 'Managed',
     'http://192.0.2.21/cgi-bin', 'cred/vms/ahm-cr-02', FALSE, 'Quarantined',
     4.0, 8, 300, 30, 10, 4, 16,
     NULL, NULL, now() - interval '2 days',
     'e0000000-0000-4000-8000-000000000002'),

    ('c1000000-0000-4000-8000-000000000005', 'VMS-SRT-TRF-01',
     'c0000000-0000-4000-8000-000000000006', 'd0000000-0000-4000-8000-000000000005',
     'Ring Road Hikvision NVR', 'HikvisionIsapi', 'Managed',
     'https://198.51.100.11/ISAPI', 'cred/vms/srt-trf-01', TRUE, 'Active',
     8.0, 16, 300, 30, 5, 6, 20,
     'worker-srt-01', now() + interval '42 seconds', now() - interval '5 days',
     'e0000000-0000-4000-8000-000000000002'),

    -- Disabled by an operator. Never claimed, never polled.
    ('c1000000-0000-4000-8000-000000000006', 'VMS-SRT-TRF-02',
     'c0000000-0000-4000-8000-000000000006', 'd0000000-0000-4000-8000-000000000006',
     'Athwalines ONVIF NVR (decommissioning)', 'Onvif', 'Managed',
     'https://198.51.100.12:8443/onvif/device_service', 'cred/vms/srt-trf-02', TRUE, 'Disabled',
     5.0, 10, 300, 30, 10, 4, 8,
     NULL, NULL, now() - interval '18 days',
     'e0000000-0000-4000-8000-000000000002'),

    ('c1000000-0000-4000-8000-000000000007', 'VMS-VAD-01',
     'c0000000-0000-4000-8000-000000000007', 'd0000000-0000-4000-8000-000000000007',
     'Alkapuri Genetec Security Center', 'GenetecWebSdk', 'Native',
     'https://203.0.113.11/WebSdk', 'cred/vms/vad-01', TRUE, 'Active',
     2.0, 4, 900, 60, 15, 2, 16,
     'worker-vad-01', now() + interval '50 seconds', now() - interval '12 days',
     'e0000000-0000-4000-8000-000000000002'),

    -- Lease expired: worker-vad-02 stopped heartbeating. Claimable again, and the fleet view
    -- should say so rather than showing it as owned.
    ('c1000000-0000-4000-8000-000000000008', 'VMS-VAD-02',
     'c0000000-0000-4000-8000-000000000007', 'd0000000-0000-4000-8000-000000000008',
     'Vadodara Station ONVIF NVR', 'Onvif', 'Managed',
     'https://203.0.113.12:8443/onvif/device_service', 'cred/vms/vad-02', FALSE, 'Active',
     5.0, 10, 300, 30, 10, 4, NULL,
     'worker-vad-02', now() - interval '4 minutes', now() - interval '6 hours',
     'e0000000-0000-4000-8000-000000000002'),

    ('c1000000-0000-4000-8000-000000000009', 'VMS-AMC-01',
     'c0000000-0000-4000-8000-000000000009', 'd0000000-0000-4000-8000-000000000009',
     'AMC Depot Dahua NVR', 'DahuaCgi', 'Managed',
     'http://192.0.2.31/cgi-bin', 'cred/vms/amc-01', FALSE, 'Active',
     4.0, 8, 300, 30, 10, 4, 10,
     'worker-ahm-01', now() + interval '36 seconds', now() - interval '4 days',
     'e0000000-0000-4000-8000-000000000002'),

    ('c1000000-0000-4000-8000-000000000010', 'VMS-AMC-02',
     'c0000000-0000-4000-8000-000000000009', 'd0000000-0000-4000-8000-000000000009',
     'AMC Civic Estate (simulated)', 'Simulator', 'Managed',
     'http://127.0.0.1:5199/sim/amc-02', 'cred/vms/amc-02', FALSE, 'Active',
     20.0, 40, 120, 15, 2, 8, 40,
     'worker-ahm-01', now() + interval '55 seconds', now() - interval '1 day',
     'e0000000-0000-4000-8000-000000000002'),

    ('c1000000-0000-4000-8000-000000000011', 'VMS-SMC-01',
     'c0000000-0000-4000-8000-000000000010', 'd0000000-0000-4000-8000-000000000005',
     'SMC Civic Estate (simulated)', 'Simulator', 'Managed',
     'http://127.0.0.1:5199/sim/smc-01', 'cred/vms/smc-01', FALSE, 'Active',
     20.0, 40, 120, 15, 2, 8, 30,
     'worker-srt-01', now() + interval '52 seconds', now() - interval '1 day',
     'e0000000-0000-4000-8000-000000000002'),

    ('c1000000-0000-4000-8000-000000000012', 'VMS-HQ-LAB-01',
     'c0000000-0000-4000-8000-000000000001', 'd0000000-0000-4000-8000-000000000010',
     'HQ Integration Lab (simulated)', 'Simulator', 'Managed',
     'http://127.0.0.1:5199/sim/lab-01', 'cred/vms/hq-lab-01', FALSE, 'Active',
     50.0, 100, 60, 10, 1, 12, 6,
     'worker-lab-01', now() + interval '58 seconds', now() - interval '2 hours',
     'e0000000-0000-4000-8000-000000000002')
ON CONFLICT (code) DO UPDATE
    SET display_name = EXCLUDED.display_name, state = EXCLUDED.state,
        leased_by = EXCLUDED.leased_by, lease_expires_at = EXCLUDED.lease_expires_at,
        last_claimed_at = EXCLUDED.last_claimed_at, updated_at = now();

-- Observability only. Ownership is decided by the lease columns above, never by this table,
-- so worker-vad-02's stale row is harmless and worth showing.
INSERT INTO worker_node (worker_id, hostname, runtime_class, vendor_filter, started_at,
                         last_heartbeat, claimed_count) VALUES
    ('worker-ahm-01', 'fed-ahm-01.trinetra.local', 'Managed',
     ARRAY['Onvif','HikvisionIsapi','DahuaCgi','Simulator']::vendor_kind[],
     now() - interval '9 days', now() - interval '4 seconds', 4),
    ('worker-ahm-02', 'fed-ahm-02.trinetra.local', 'Native',
     ARRAY['MilestoneGateway']::vendor_kind[],
     now() - interval '9 days', now() - interval '6 seconds', 1),
    ('worker-srt-01', 'fed-srt-01.trinetra.local', 'Managed',
     ARRAY['Onvif','HikvisionIsapi','Simulator']::vendor_kind[],
     now() - interval '5 days', now() - interval '3 seconds', 2),
    ('worker-vad-01', 'fed-vad-01.trinetra.local', 'Native',
     ARRAY['GenetecWebSdk']::vendor_kind[],
     now() - interval '12 days', now() - interval '8 seconds', 1),
    -- Dead: no heartbeat for hours, which is why VMS-VAD-02's lease lapsed.
    ('worker-vad-02', 'fed-vad-02.trinetra.local', 'Managed',
     ARRAY['Onvif']::vendor_kind[],
     now() - interval '12 days', now() - interval '6 hours', 0),
    ('worker-lab-01', 'fed-lab-01.trinetra.local', 'Managed',
     ARRAY['Simulator']::vendor_kind[],
     now() - interval '2 hours', now() - interval '2 seconds', 1)
ON CONFLICT (worker_id) DO UPDATE
    SET last_heartbeat = EXCLUDED.last_heartbeat, claimed_count = EXCLUDED.claimed_count;

-- Capability bitmasks, per Capability.cs:
--   Inventory 1, CameraStatus 2, Streams 4, Recordings 8, RecordingExport 16, EventsPull 32,
--   EventsSubscribe 64, Metadata 128, Ptz 256, Snapshot 512, TimeSyncCheck 1024
-- No VMS supports everything, and a UI that assumes otherwise breaks on the first ONVIF device.
INSERT INTO connector_capability (target_id, supported, adapter_version, probed_at, notes)
SELECT t.id,
       CASE t.vendor
           WHEN 'Onvif'            THEN 871   -- no recordings, no metadata, no time sync
           WHEN 'HikvisionIsapi'   THEN 1983  -- no event subscription
           WHEN 'DahuaCgi'         THEN 815
           WHEN 'MilestoneGateway' THEN 1791  -- no PTZ through the gateway
           WHEN 'GenetecWebSdk'    THEN 1023
           WHEN 'Simulator'        THEN 1639
       END,
       CASE t.vendor
           WHEN 'Onvif'            THEN '1.4.0'
           WHEN 'HikvisionIsapi'   THEN '2.1.3'
           WHEN 'DahuaCgi'         THEN '1.9.2'
           WHEN 'MilestoneGateway' THEN '3.0.1'
           WHEN 'GenetecWebSdk'    THEN '2.7.0'
           WHEN 'Simulator'        THEN '1.0.0'
       END,
       now() - interval '6 hours',
       jsonb_build_object(
           'probeDurationMs', 400 + (t.rate_limit_burst * 7),
           'firmwareFamily', t.vendor::text,
           'note', 'Seeded by db/seed/dev-sample-data.sql; not a real probe.')
FROM connector_target t
WHERE t.id::text LIKE 'c1000000-%'
ON CONFLICT (target_id) DO UPDATE
    SET supported = EXCLUDED.supported, adapter_version = EXCLUDED.adapter_version,
        probed_at = EXCLUDED.probed_at, notes = EXCLUDED.notes;

-- Cursors for everything that is actually polled. The quarantined target keeps a stale cursor,
-- which is what a long lag looks like in the health view.
INSERT INTO connector_cursor (target_id, cursor_timestamp, cursor_event_id, max_lookback_hours, updated_at)
SELECT t.id,
       CASE WHEN t.state = 'Quarantined'
            THEN now() - interval '2 days'
            ELSE now() - (t.event_poll_seconds * interval '1 second') END,
       'SRC-' || upper(substr(md5(t.code), 1, 12)),
       CASE WHEN t.vendor = 'Simulator' THEN 2 ELSE 6 END,
       now() - interval '30 seconds'
FROM connector_target t
WHERE t.id::text LIKE 'c1000000-%' AND t.state <> 'Disabled'
ON CONFLICT (target_id) DO UPDATE
    SET cursor_timestamp = EXCLUDED.cursor_timestamp, updated_at = EXCLUDED.updated_at;


-- ===========================================================================
-- 7. federated cameras
-- ===========================================================================
-- 230 cameras across the twelve targets. camera_id stays NULL throughout: it is Model 1's
-- registry id, resolved by reconciliation, and Model 1 does not exist yet. The
-- ix_camera_unreconciled index exists precisely for this state, so the UI's reconciliation
-- backlog should show everything.

WITH counts(code, cam_count) AS (VALUES
    ('VMS-AHM-TRF-01', 24), ('VMS-AHM-TRF-02', 18), ('VMS-AHM-CR-01', 32),
    ('VMS-AHM-CR-02',  12), ('VMS-SRT-TRF-01', 20), ('VMS-SRT-TRF-02',  8),
    ('VMS-VAD-01',     16), ('VMS-VAD-02',     14), ('VMS-AMC-01',     10),
    ('VMS-AMC-02',     40), ('VMS-SMC-01',     30), ('VMS-HQ-LAB-01',   6)
)
INSERT INTO federated_camera (
    target_id, native_camera_id, camera_id, organization_unit_id, site_id,
    name, vendor_model, firmware, latitude, longitude,
    is_enabled, is_recording, health, last_seen, stream_references, raw_reference,
    first_seen_at, updated_at)
SELECT
    t.id,
    'CH' || lpad(n::text, 3, '0'),
    NULL,
    t.organization_unit_id,
    t.site_id,
    format('%s — Channel %s', t.display_name, lpad(n::text, 2, '0')),
    CASE t.vendor
        WHEN 'Onvif'            THEN 'Generic ONVIF Profile S'
        WHEN 'HikvisionIsapi'   THEN 'DS-2CD2T47G2-L'
        WHEN 'DahuaCgi'         THEN 'IPC-HFW3849T1'
        WHEN 'MilestoneGateway' THEN 'XProtect Channel'
        WHEN 'GenetecWebSdk'    THEN 'Security Center Unit'
        WHEN 'Simulator'        THEN 'Simulated Camera'
    END,
    format('%s.%s.%s', 5 + (n % 3), 1 + (n % 7), n % 10),
    -- Spread around the site centre: several cameras at one junction sit on different poles.
    COALESCE(s.latitude,  23.0225000) + ((n % 7) - 3) * 0.0009,
    COALESCE(s.longitude, 72.5714000) + ((n % 5) - 2) * 0.0011,
    t.state <> 'Disabled' AND n % 23 <> 0,
    t.state = 'Active' AND n % 19 <> 0,
    CASE
        WHEN t.state = 'Disabled'    THEN 'Unknown'::health_status
        WHEN t.state = 'Quarantined' THEN 'AuthFailed'::health_status
        WHEN n % 17 = 0              THEN 'Unreachable'::health_status
        WHEN n % 11 = 0              THEN 'Degraded'::health_status
        ELSE 'Healthy'::health_status
    END,
    CASE
        WHEN t.state = 'Disabled'    THEN now() - interval '18 days'
        WHEN t.state = 'Quarantined' THEN now() - interval '2 days'
        WHEN n % 17 = 0              THEN now() - ((n % 300) * interval '1 minute')
        ELSE now() - ((n % 90) * interval '1 second')
    END,
    ARRAY[
        format('rtsp://%s/%s/main', lower(t.code), 'CH' || lpad(n::text, 3, '0')),
        format('rtsp://%s/%s/sub',  lower(t.code), 'CH' || lpad(n::text, 3, '0'))
    ],
    format('vms://%s/cameras/%s', lower(t.code), 'CH' || lpad(n::text, 3, '0')),
    now() - interval '30 days',
    now() - interval '5 minutes'
FROM connector_target t
JOIN counts c ON c.code = t.code
LEFT JOIN sites s ON s.id = t.site_id
CROSS JOIN LATERAL generate_series(1, c.cam_count) AS n
ON CONFLICT (target_id, native_camera_id) DO UPDATE
    SET health = EXCLUDED.health, last_seen = EXCLUDED.last_seen,
        is_enabled = EXCLUDED.is_enabled, is_recording = EXCLUDED.is_recording,
        updated_at = EXCLUDED.updated_at;


-- ===========================================================================
-- 8. secrets
-- ===========================================================================
-- PLACEHOLDER CIPHERTEXT — see the header. These make `GET /vms/{id}/credential/status` report
-- exists: true; they will not decrypt. VMS-AHM-CR-02 is left without one deliberately, so the
-- "registered but never provisioned" case has an example.

INSERT INTO secret (credential_reference, ciphertext, nonce, tag, key_id, description,
                    created_at, updated_at, rotated_at)
SELECT t.credential_reference,
       decode(md5(t.code) || md5(t.code || 'x'), 'hex'),      -- 32 bytes of filler
       decode(substr(md5(t.code || 'nonce'), 1, 24), 'hex'),  -- exactly 12 bytes
       decode(substr(md5(t.code || 'tag'),   1, 32), 'hex'),  -- exactly 16 bytes
       'demo-key-2026-01',
       format('Placeholder credential for %s. Seeded, not sealed — will not decrypt.', t.code),
       now() - interval '30 days',
       now() - interval '30 days',
       NULL
FROM connector_target t
WHERE t.id::text LIKE 'c1000000-%'
  AND t.code <> 'VMS-AHM-CR-02'
ON CONFLICT (credential_reference) DO UPDATE
    SET description = EXCLUDED.description, updated_at = EXCLUDED.updated_at;


-- ===========================================================================
-- 9. api keys
-- ===========================================================================
-- Machine-to-machine only; people use JWT. key_hash is lowercase hex SHA-256 of the presented
-- value, matching ApiKeyAuthenticationHandler.
--
-- Plaintext keys, for development calls with the X-Api-Key header:
--
--     trn_dev_dashboard_key_0000000001   -> GRP-ANALYSTS   (read-only, unscoped)
--     trn_dev_ingest_key_0000000002      -> GRP-SRT-OPS    (scoped to Surat City Police)
--
-- The scoped key is the interesting one: it must see Surat targets and nothing else. A key that
-- reaches the whole estate proves very little.

INSERT INTO api_key (id, key_id, key_hash, display_name, group_id,
                     created_by, expires_at, revoked_at, last_used_at) VALUES
    ('a2000000-0000-4000-8000-000000000001', 'dev-dashboard',
     '5c2d4eec498c48aaacad41804585510d7b2e2986c61b42a510909211f6823880',
     'Development dashboard (read-only)', 'a1000000-0000-4000-8000-000000000008',
     'e0000000-0000-4000-8000-000000000001', now() + interval '180 days', NULL,
     now() - interval '11 minutes'),

    ('a2000000-0000-4000-8000-000000000002', 'dev-surat-ingest',
     '025b717920eaa91873a4be0262de10a0bc7d7f3cabd87fcb89bf071a08def592',
     'Surat ingest integration (scoped)', 'a1000000-0000-4000-8000-000000000004',
     'e0000000-0000-4000-8000-000000000001', now() + interval '90 days', NULL,
     now() - interval '2 hours'),

    -- Revoked. Authentication must fail identically to an unknown key.
    ('a2000000-0000-4000-8000-000000000003', 'dev-retired-key',
     'a3f5c1d9b7e42068f1c3aa5e9d0b7742c8e6031594ab7d2f6e08c15a93b4d770',
     'Retired vendor integration', 'a1000000-0000-4000-8000-000000000011',
     'e0000000-0000-4000-8000-000000000001', NULL, now() - interval '14 days',
     now() - interval '15 days')
ON CONFLICT (key_id) DO UPDATE
    SET key_hash = EXCLUDED.key_hash, group_id = EXCLUDED.group_id,
        expires_at = EXCLUDED.expires_at, revoked_at = EXCLUDED.revoked_at;


-- ===========================================================================
-- 10. connector health history
-- ===========================================================================
-- Seven days at 30-minute resolution for all twelve targets — 4,032 rows. cursor_lag_seconds is
-- the signal worth rendering: a target returning 200 OK while hours behind is the failure the
-- rest of the view hides.

INSERT INTO connector_health (
    target_id, checked_at, status, latency_ms, camera_count,
    consecutive_failures, circuit_open, last_error, events_since_check, cursor_lag_seconds)
SELECT
    t.id,
    date_trunc('hour', now()) - (i * interval '30 minutes'),
    st.status,
    CASE st.status
        WHEN 'Healthy'::health_status     THEN 35.0 + ((i * 7) % 90)
        WHEN 'Degraded'::health_status    THEN 380.0 + ((i * 13) % 900)
        WHEN 'AuthFailed'::health_status  THEN 55.0 + ((i * 3) % 40)
        ELSE NULL
    END,
    CASE WHEN st.status IN ('Unreachable'::health_status, 'AuthFailed'::health_status)
         THEN NULL ELSE cam.cam_count END,
    CASE WHEN st.status = 'Healthy'::health_status THEN 0 ELSE (i % 6) + 1 END,
    t.state = 'Quarantined' AND i < 8,
    CASE st.status
        WHEN 'Unreachable'::health_status THEN 'Connection timed out after 10,000 ms.'
        WHEN 'AuthFailed'::health_status  THEN 'HTTP 401 from the device. Credential rejected; not retried.'
        WHEN 'Degraded'::health_status    THEN 'Inventory poll exceeded its budget; partial channel list returned.'
        ELSE NULL
    END,
    CASE st.status
        WHEN 'Healthy'::health_status  THEN ((i * 11) % 45)::bigint
        WHEN 'Degraded'::health_status THEN ((i * 3) % 12)::bigint
        ELSE 0::bigint
    END,
    CASE st.status
        WHEN 'Healthy'::health_status     THEN 1.0 + ((i * 5) % 14)
        WHEN 'Degraded'::health_status    THEN 90.0 + ((i * 29) % 800)
        WHEN 'Unreachable'::health_status THEN 1800.0 + ((i * 61) % 5400)
        WHEN 'AuthFailed'::health_status  THEN 172800.0
        ELSE NULL
    END
FROM connector_target t
JOIN (VALUES
    ('VMS-AHM-TRF-01', 24), ('VMS-AHM-TRF-02', 18), ('VMS-AHM-CR-01', 32),
    ('VMS-AHM-CR-02',  12), ('VMS-SRT-TRF-01', 20), ('VMS-SRT-TRF-02',  8),
    ('VMS-VAD-01',     16), ('VMS-VAD-02',     14), ('VMS-AMC-01',     10),
    ('VMS-AMC-02',     40), ('VMS-SMC-01',     30), ('VMS-HQ-LAB-01',   6)
) AS cam(code, cam_count) ON cam.code = t.code
CROSS JOIN generate_series(0, 335) AS i
CROSS JOIN LATERAL (
    SELECT CASE
        WHEN t.state = 'Disabled'          THEN 'Unknown'::health_status
        WHEN t.state = 'Quarantined'       THEN 'AuthFailed'::health_status
        WHEN (i + cam.cam_count) % 53 = 0  THEN 'Unreachable'::health_status
        WHEN (i + cam.cam_count) % 17 = 0  THEN 'Degraded'::health_status
        ELSE 'Healthy'::health_status
    END AS status
) AS st
WHERE t.id::text LIKE 'c1000000-%'
ON CONFLICT (target_id, checked_at) DO NOTHING;


-- ===========================================================================
-- 11. normalised events
-- ===========================================================================
-- ~6,480 events spread over the last six days, one every 80 seconds, across every camera on an
-- Active target.
--
-- object_reference draws from a pool of 40 number plates and 24 person references, deliberately
-- reused across cameras and times. That is what makes the correlation view show anything: the
-- same plate appearing on three cameras within a window is the whole point of ix_event_object_time.
--
-- Cross-camera identity is PROBABILISTIC. confidence is populated on every detection and must
-- never be rendered as certainty.

WITH cam AS (
    SELECT row_number() OVER (ORDER BY fc.target_id, fc.native_camera_id) AS rn,
           fc.target_id, fc.native_camera_id, fc.organization_unit_id, fc.site_id,
           fc.latitude, fc.longitude
    FROM federated_camera fc
    JOIN connector_target t ON t.id = fc.target_id
    WHERE t.id::text LIKE 'c1000000-%' AND t.state = 'Active'
),
total AS (SELECT count(*)::int AS c FROM cam),
series AS (
    SELECT i, date_trunc('hour', now()) - (i * interval '80 seconds') AS occurred_at
    FROM generate_series(1, 6480) AS i
),
ev AS (
    SELECT s.i,
           s.occurred_at,
           c.target_id, c.native_camera_id, c.organization_unit_id, c.site_id,
           c.latitude, c.longitude,
           (ARRAY[
               'MotionDetected','MotionDetected','MotionDetected',
               'AnprDetection','AnprDetection','AnprDetection',
               'VehicleDetection','VehicleDetection',
               'PersonDetection','PersonDetection',
               'LineCrossed','FaceDetection','IntrusionDetected','LoiteringDetected',
               'CrowdDetected','TamperDetected','VideoLoss','StreamLost','StreamRestored',
               'CameraOffline','CameraOnline','RecordingStarted','StorageFailure','SceneChange'
           ])[(s.i % 24) + 1] AS event_type
    FROM series s
    CROSS JOIN total
    JOIN cam c ON c.rn = ((s.i * 7) % total.c) + 1
)
INSERT INTO federation_event (
    event_id, source_vms_id, source_event_id, camera_id,
    organization_unit_id, site_id, event_type, vendor_event_type, occurred_at, severity,
    object_reference, confidence, latitude, longitude,
    raw_reference, delivery_mode, trace_id, received_at)
SELECT
    'EVT-' || md5(format('trinetra-demo|%s|%s|%s', ev.i, ev.target_id, ev.native_camera_id)),
    ev.target_id,
    'SRC-' || lpad(ev.i::text, 8, '0'),
    ev.native_camera_id,
    ev.organization_unit_id,
    ev.site_id,
    ev.event_type,
    -- The vendor's own label is preserved even when the normalised type fits, because losing it
    -- is what makes an event unexplainable six months later.
    CASE ev.event_type
        WHEN 'MotionDetected'    THEN 'VMD'
        WHEN 'AnprDetection'     THEN 'LPR_PLATE_READ'
        WHEN 'LineCrossed'       THEN 'fielddetection'
        WHEN 'IntrusionDetected' THEN 'regionEntrance'
        WHEN 'TamperDetected'    THEN 'shelteralarm'
        WHEN 'VideoLoss'         THEN 'videoloss'
        ELSE NULL
    END,
    ev.occurred_at,
    CASE ev.event_type
        WHEN 'TamperDetected'    THEN 'High'
        WHEN 'IntrusionDetected' THEN 'High'
        WHEN 'StorageFailure'    THEN 'Critical'
        WHEN 'CameraOffline'     THEN 'High'
        WHEN 'VideoLoss'         THEN 'High'
        WHEN 'StreamLost'        THEN 'Medium'
        WHEN 'LoiteringDetected' THEN 'Medium'
        WHEN 'CrowdDetected'     THEN 'Medium'
        WHEN 'LineCrossed'       THEN 'Low'
        WHEN 'AnprDetection'     THEN 'Low'
        ELSE 'Info'
    END,
    CASE
        WHEN ev.event_type = 'AnprDetection' THEN
            'GJ' || lpad((((ev.i % 40) % 12) + 1)::text, 2, '0')
                 || (ARRAY['AB','CD','EF','GH','JK'])[((ev.i % 40) % 5) + 1]
                 || lpad((1000 + ((ev.i % 40) * 211) % 9000)::text, 4, '0')
        WHEN ev.event_type IN ('PersonDetection','FaceDetection') THEN
            'PSN-' || lpad(((ev.i % 24) + 1)::text, 4, '0')
        WHEN ev.event_type = 'VehicleDetection' THEN
            'VEH-' || lpad(((ev.i % 30) + 1)::text, 4, '0')
        ELSE NULL
    END,
    CASE
        WHEN ev.event_type IN ('AnprDetection','PersonDetection','FaceDetection','VehicleDetection')
        THEN round((0.62 + ((ev.i % 34) / 100.0))::numeric, 2)::double precision
        ELSE NULL
    END,
    ev.latitude,
    ev.longitude,
    format('vms://%s/events/%s', ev.target_id, 'SRC-' || lpad(ev.i::text, 8, '0')),
    -- Backfill after a reconnect delivers at many times the live rate. Consumers that alert or
    -- page have to be able to tell the two apart.
    CASE WHEN ev.i BETWEEN 900 AND 1000 THEN 'Backfill' ELSE 'Live' END,
    md5(format('trace|%s|%s', ev.i, ev.target_id)),
    ev.occurred_at + interval '1.2 seconds'
FROM ev
ON CONFLICT DO NOTHING;

-- A handful of payloads that could not be normalised. Retained so a mapping bug is fixed by
-- replay rather than by re-pulling from a VMS that has aged the event out.
INSERT INTO federation_event_deadletter (target_id, received_at, reason, raw_reference, raw_payload)
SELECT t.id,
       now() - (n * interval '4 hours'),
       (ARRAY[
           'Unmapped vendor event type ''smartMotionHuman''; no NormalisedEvent.EventType fits.',
           'occurred_at absent from the vendor payload and no fallback available.',
           'Channel id ''0'' does not correspond to any known camera on this target.'
       ])[(n % 3) + 1],
       format('vms://%s/events/raw/%s', lower(t.code), n),
       jsonb_build_object(
           'vendor', t.vendor::text,
           'channel', n,
           'rawType', (ARRAY['smartMotionHuman','crossRegionDetection','unknown'])[(n % 3) + 1],
           'receivedAt', (now() - (n * interval '4 hours'))::text)
FROM connector_target t
CROSS JOIN generate_series(1, 3) AS n
WHERE t.code IN ('VMS-AHM-TRF-02', 'VMS-VAD-01', 'VMS-AMC-01')
  AND NOT EXISTS (
      SELECT 1 FROM federation_event_deadletter d
      WHERE d.raw_reference = format('vms://%s/events/raw/%s', lower(t.code), n));


-- ===========================================================================
-- 12. connection tests
-- ===========================================================================
-- Every terminal status plus one still running, so the polling UI has something to poll.

INSERT INTO connection_test (id, target_id, status, requested_by, requested_at,
                             executed_by, started_at, completed_at, result, failure_reason) VALUES
    ('a3000000-0000-4000-8000-000000000001', 'c1000000-0000-4000-8000-000000000001',
     'completed', 'vms.admin', now() - interval '3 hours',
     'api-01', now() - interval '3 hours', now() - interval '3 hours' + interval '4 seconds',
     jsonb_build_object(
         'reachable', true, 'authenticated', true, 'latencyMs', 148,
         'cameraCount', 24, 'adapterVersion', '1.4.0',
         'capabilities', jsonb_build_array('Inventory','CameraStatus','Streams','EventsPull','EventsSubscribe','Ptz','Snapshot')),
     NULL),

    ('a3000000-0000-4000-8000-000000000002', 'c1000000-0000-4000-8000-000000000003',
     'completed', 'ahmedabad.admin', now() - interval '1 day',
     'api-02', now() - interval '1 day', now() - interval '1 day' + interval '11 seconds',
     jsonb_build_object(
         'reachable', true, 'authenticated', true, 'latencyMs', 612,
         'cameraCount', 32, 'adapterVersion', '3.0.1',
         'warnings', jsonb_build_array('Recording export is licensed but disabled on this server.')),
     NULL),

    -- The quarantined target. Its credential was never written, which is the ordinary cause.
    ('a3000000-0000-4000-8000-000000000003', 'c1000000-0000-4000-8000-000000000004',
     'failed', 'vms.admin', now() - interval '2 days',
     'api-01', now() - interval '2 days', now() - interval '2 days' + interval '9 seconds',
     jsonb_build_object('reachable', true, 'authenticated', false, 'latencyMs', 74),
     'AuthException: the device returned HTTP 401. Not retried — a rejected credential replayed '
     || 'across the estate locks the integration account out everywhere.'),

    -- The instance running this one died. sweep_abandoned_connection_tests() produced this row.
    ('a3000000-0000-4000-8000-000000000004', 'c1000000-0000-4000-8000-000000000008',
     'abandoned', 'state.admin', now() - interval '5 hours',
     'api-03', now() - interval '5 hours', now() - interval '5 hours' + interval '5 minutes',
     NULL,
     'No result after 00:05:00. The API instance running this test most likely stopped.'),

    ('a3000000-0000-4000-8000-000000000005', 'c1000000-0000-4000-8000-000000000012',
     'running', 'vms.admin', now() - interval '20 seconds',
     'api-01', now() - interval '18 seconds', NULL, NULL, NULL)
ON CONFLICT (id) DO UPDATE
    SET status = EXCLUDED.status, requested_at = EXCLUDED.requested_at,
        started_at = EXCLUDED.started_at, completed_at = EXCLUDED.completed_at,
        result = EXCLUDED.result, failure_reason = EXCLUDED.failure_reason;


-- ===========================================================================
-- 13. audit
-- ===========================================================================
-- Both audit tables use identity primary keys, so re-running would duplicate rather than
-- conflict. The seeded rows are deleted first, identified by markers reserved for this file:
-- source_address in the RFC 5737 documentation range 198.51.100.x, and accessed_by ending
-- '@demo'. Nothing the application writes carries either.

DELETE FROM config_audit WHERE source_address LIKE '198.51.100.%';
DELETE FROM credential_access_log WHERE accessed_by LIKE '%@demo';

INSERT INTO config_audit (changed_at, actor, actor_user_id, actor_api_key_id, action,
                          entity_type, entity_id, organization_unit_id,
                          before_state, after_state, source_address)
SELECT
    now() - (n * interval '3 days'),
    (ARRAY['vms.admin','ahmedabad.admin','state.admin','surat.operator'])[(n % 4) + 1],
    (ARRAY[
        'e0000000-0000-4000-8000-000000000002',
        'e0000000-0000-4000-8000-000000000003',
        'e0000000-0000-4000-8000-000000000001',
        'e0000000-0000-4000-8000-000000000004'
    ])[(n % 4) + 1]::uuid,
    NULL,
    (ARRAY['update','create','update','credential_set'])[(n % 4) + 1],
    'connector_target',
    t.id::text,
    t.organization_unit_id,
    CASE WHEN (n % 4) + 1 = 2 THEN NULL
         ELSE jsonb_build_object('state', 'Active', 'rateLimitPerSecond', 5.0) END,
    -- Credential material is NEVER recorded. A credential_set row says that it changed, not what to.
    CASE WHEN (n % 4) + 1 = 4
         THEN jsonb_build_object('credentialReference', t.credential_reference, 'value', 'redacted')
         ELSE jsonb_build_object('state', t.state::text,
                                 'rateLimitPerSecond', t.rate_limit_per_second) END,
    '198.51.100.' || (10 + (n % 6))::text
FROM connector_target t
CROSS JOIN generate_series(1, 2) AS n
WHERE t.id::text LIKE 'c1000000-%';

INSERT INTO config_audit (changed_at, actor, actor_user_id, actor_api_key_id, action,
                          entity_type, entity_id, organization_unit_id,
                          before_state, after_state, source_address) VALUES
    (now() - interval '40 days', 'state.admin', 'e0000000-0000-4000-8000-000000000001', NULL,
     'create', 'access_group', 'a1000000-0000-4000-8000-000000000006', NULL,
     NULL, jsonb_build_object('code', 'GRP-INVESTIGATIONS', 'role', 'INVESTIGATOR', 'status', 'ACTIVE'),
     '198.51.100.10'),
    (now() - interval '14 days', 'state.admin', 'e0000000-0000-4000-8000-000000000001', NULL,
     'update', 'api_key', 'a2000000-0000-4000-8000-000000000003', NULL,
     jsonb_build_object('revokedAt', NULL), jsonb_build_object('revokedAt', 'set'),
     '198.51.100.10'),
    (now() - interval '2 days', 'vms.admin', 'e0000000-0000-4000-8000-000000000002', NULL,
     'update', 'connector_target', 'c1000000-0000-4000-8000-000000000004',
     'c0000000-0000-4000-8000-000000000004',
     jsonb_build_object('state', 'Active'), jsonb_build_object('state', 'Quarantined'),
     '198.51.100.11'),
    (now() - interval '6 hours', 'dev-surat-ingest', NULL,
     'a2000000-0000-4000-8000-000000000002',
     'update', 'connector_target', 'c1000000-0000-4000-8000-000000000005',
     'c0000000-0000-4000-8000-000000000006',
     jsonb_build_object('eventPollSeconds', 10), jsonb_build_object('eventPollSeconds', 5),
     '198.51.100.20');

-- Repeated failures against one reference mean either a rotated secret nobody updated, or
-- someone probing. Both are worth an alert, so both need an example.
INSERT INTO credential_access_log (accessed_at, credential_reference, accessed_by,
                                   target_id, succeeded, failure_reason)
SELECT
    now() - (n * interval '90 minutes'),
    t.credential_reference,
    CASE WHEN t.runtime_class = 'Native' THEN 'worker-vad-01@demo' ELSE 'worker-ahm-01@demo' END,
    t.id,
    t.state <> 'Quarantined',
    CASE WHEN t.state = 'Quarantined'
         THEN 'Decryption succeeded; the device rejected the credential (HTTP 401).'
         ELSE NULL END
FROM connector_target t
CROSS JOIN generate_series(1, 4) AS n
WHERE t.id::text LIKE 'c1000000-%' AND t.state <> 'Disabled';

INSERT INTO credential_access_log (accessed_at, credential_reference, accessed_by,
                                   target_id, succeeded, failure_reason)
SELECT now() - (n * interval '7 minutes'),
       'cred/vms/ahm-cr-02', 'worker-ahm-02@demo',
       'c1000000-0000-4000-8000-000000000004', FALSE,
       'No secret stored under this reference. The target was registered but never provisioned.'
FROM generate_series(1, 9) AS n;


COMMIT;


-- ===========================================================================
-- What was loaded
-- ===========================================================================
-- Run this afterwards to confirm; it is a plain SELECT and safe to repeat.
--
--   SELECT 'organizations',      count(*) FROM federation.organizations
--   UNION ALL SELECT 'org units', count(*) FROM federation.organization_units
--   UNION ALL SELECT 'areas',     count(*) FROM federation.geographic_areas
--   UNION ALL SELECT 'sites',     count(*) FROM federation.sites
--   UNION ALL SELECT 'users',     count(*) FROM federation.platform_users
--   UNION ALL SELECT 'groups',    count(*) FROM federation.access_groups
--   UNION ALL SELECT 'targets',   count(*) FROM federation.connector_target
--   UNION ALL SELECT 'cameras',   count(*) FROM federation.federated_camera
--   UNION ALL SELECT 'health',    count(*) FROM federation.connector_health
--   UNION ALL SELECT 'events',    count(*) FROM federation.federation_event
--   ORDER BY 1;
--
-- And the condition that silently degrades every event query — this must stay 0:
--
--   SELECT * FROM federation.event_partition_health;


-- ===========================================================================
-- Teardown
-- ===========================================================================
-- Removes everything this file inserted, in FK order. Uncomment to use.
--
-- BEGIN;
-- SET search_path TO federation, public;
--
-- DELETE FROM credential_access_log WHERE accessed_by LIKE '%@demo';
-- DELETE FROM config_audit          WHERE source_address LIKE '198.51.100.%';
-- DELETE FROM federation_event      WHERE source_vms_id::text LIKE 'c1000000-%';
-- DELETE FROM federation_event_deadletter WHERE target_id::text LIKE 'c1000000-%';
-- DELETE FROM connection_test       WHERE id::text LIKE 'a3000000-%';
-- -- connector_health, connector_capability, connector_cursor and federated_camera all cascade
-- -- from connector_target.
-- DELETE FROM connector_target      WHERE id::text LIKE 'c1000000-%';
-- DELETE FROM worker_node           WHERE worker_id LIKE 'worker-%';
-- DELETE FROM secret                WHERE key_id = 'demo-key-2026-01';
-- DELETE FROM api_key               WHERE id::text LIKE 'a2000000-%';
-- DELETE FROM user_groups           WHERE id::text LIKE 'a4000000-%';
-- DELETE FROM group_scopes          WHERE group_id::text LIKE 'a1000000-%';
-- DELETE FROM access_groups         WHERE id::text LIKE 'a1000000-%';
-- DELETE FROM scopes                WHERE id::text LIKE 'f0000000-%';
-- DELETE FROM platform_users        WHERE id::text LIKE 'e0000000-%';
-- DELETE FROM sites                 WHERE id::text LIKE 'd0000000-%';
-- DELETE FROM organization_units    WHERE id::text LIKE 'c0000000-%';
-- DELETE FROM organizations         WHERE id::text LIKE 'b0000000-%';
-- DELETE FROM geographic_areas      WHERE id::text LIKE 'a0000000-%';
--
-- COMMIT;
