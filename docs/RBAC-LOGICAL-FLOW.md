# Trinetra RBAC — Logical Flow

## 1. Objective

Trinetra uses a **Group-based RBAC model** where a user receives access through membership in one or more Access Groups.

The model separates:

- **User** — who is accessing the platform
- **Access Group** — reusable access bundle
- **Role** — what actions are allowed
- **Permission** — specific system action
- **Scope** — where or on which resources the permission applies
- **Resource** — camera, VMS, event, observation, alert, etc.

Core relationship:

```text
User
  |
  v
Access Group
  |
  +------------------+
  |                  |
  v                  v
Role               Scope
  |                  |
  v                  +----------------------+
Permissions                                 |
                                             v
                                  Organization / Geography /
                                  Resource
```

## 2. Core Access Model

```text
WHO
 |
 User
 |
 v
WHICH ACCESS GROUP?
 |
 v
WHAT CAN THEY DO?
 |
 Role
 |
 Permissions
 |
 v
WHERE / ON WHAT?
 |
 Scope
 |
 v
RESOURCE
 |
 v
ALLOW / DENY
```

**Role determines what a user can do; Scope determines where or on which resources they can do it.**

## 3. Main Entities

### User

A user can belong to multiple Access Groups.

```text
User
 |
 +--> Group A
 +--> Group B
 +--> Group C
```

### Access Group

An Access Group is a reusable combination of a Role and one or more Scopes.

Example:

```text
Group:
Ahmedabad Police Camera Operators

Role:
CAMERA_OPERATOR

Scopes:
Police Department
Ahmedabad District
```

### Role

Examples:

```text
SUPER_ADMIN
STATE_ADMIN
DEPARTMENT_ADMIN
CAMERA_OPERATOR
INVESTIGATOR
MAINTENANCE_OPERATOR
VMS_OPERATOR
VMS_ADMIN
ANALYST
VIEWER
```

The roles above ship as **presets** — a starting point, not a fixed set. `role.manage` (a new
permission) allows creating custom roles and re-composing the presets, via
`POST` / `PUT` / `DELETE /api/v1/roles`. Three rules protect the platform:

- **`SUPER_ADMIN` is immutable** through the API — it is the recovery role the first-start
  backfill and the platform-admin group depend on.
- A role is a **global** object with no scope of its own, so editing a preset changes every
  access group built on it in every organization. **Editing, disabling or deleting a preset
  (`is_system`) therefore requires `role.manage` held _unscoped_** — a scoped department admin
  cannot re-compose or disable a role the whole estate uses. Scoped `role.manage` holders are
  limited to custom roles.
- A caller who is **not** unscoped for `role.manage` may only touch a permission the caller
  already holds — on the new set *and* on the role's existing set. Otherwise `role.manage` would
  be "grant yourself anything": compose the permission into a role, attach a group in your own
  scope, add yourself. The check runs against the role row read `FOR UPDATE`.

A preset's `code` never changes (migrations reference presets by code) and a preset cannot be
deleted, only disabled. An operator edit that actually changes a preset's name, description or
permission set stamps `roles.customized_at`; a later migration re-seeding preset data must skip
customized rows so a deliberate change is not silently reverted.

A Role should not contain geographic information.

### Permission

Permissions represent concrete actions.

```text
camera.read
camera.create
camera.update
camera.delete
camera.import
camera.reconcile
camera.health.read
camera.maintenance.read
camera.maintenance.update

video.read
video.playback

observation.read
observation.search

event.read
event.acknowledge

alert.read
alert.acknowledge
alert.resolve

vms.read
vms.create
vms.update

adapter.read
adapter.create
adapter.update

integration.manage

gis.read
gis.coverage.read

vehicle_search.execute
person_search.execute

audit.read
```

## 4. Scope Model

Scope defines the boundary in which permissions apply.

Supported dimensions:

```text
Organization Scope
Geographic Scope
Resource Scope
```

Examples:

```text
Organization:
Police Department
Transport Department
Municipal Corporation

Geography:
Gujarat
Ahmedabad District
Daskroi Taluka
Village X
Site Y

Resource:
Specific Camera
Specific VMS
Specific Adapter
```

## 5. Organization and Geography Are Separate

Do not create:

```text
Department
  -> District
      -> Taluka
          -> Village
```

Instead:

```text
Organization                    Geography
     |                              |
Police Department               Gujarat
     |                              |
Organization Unit               Ahmedabad
     |                              |
     |                           Daskroi
     |                              |
     +-------- Camera -------------+
```

A camera connects both dimensions:

```text
Camera
 |
 +--> Organization Unit
 |
 +--> Site
```

## 6. User → Group Assignment

```text
users
  |
  v
user_groups
  |
  v
access_groups
```

Example:

```text
Rahul
 |
 +--> Ahmedabad Police Camera Operators
 |
 +--> VMS Integration Team
```

This avoids creating custom roles for every combination of responsibility and location.

## 7. End-to-End Authorization Flow

```text
Client
  |
  | HTTP Request + Access Token
  v
API Gateway / Backend
  |
  v
Authentication
  |
  +---- INVALID ----> 401 Unauthorized
  |
  VALID
  |
  v
Identify User
  |
  v
Load User's Access Groups
  |
  v
Load Roles
  |
  v
Load Permissions
  |
  v
Resolve Requested Resource
  |
  v
Evaluate Scope
  |
  v
Authorization Decision
  |
  +---- DENY ----> 403 Forbidden
  |
  ALLOW
  |
  v
Execute Business Operation
  |
  v
Audit Sensitive Operation
  |
  v
Response
```

## 8. Authentication vs Authorization

Authentication answers:

> Who are you?

Authorization answers:

> What are you allowed to do?

```text
Authentication:
Login
  ↓
Identity Provider
  ↓
Access Token
  ↓
User ID

Authorization:
User ID
  ↓
Access Groups
  ↓
Role
  ↓
Permissions
  ↓
Scopes
  ↓
Resource
```

## 9. API Authorization Example

Request:

```http
GET /api/v1/cameras/CAM-001
Authorization: Bearer <token>
```

Processing:

```text
1. Validate token
2. Identify user
3. Find user's groups
4. Find group role
5. Check required permission: camera.read
6. Load CAM-001
7. Evaluate organization scope
8. Evaluate geographic/resource scope
9. ALLOW or DENY
```

## 10. Example Authorization Decision

User:

```text
Rahul
```

Group:

```text
Ahmedabad Police Camera Operators
```

Role:

```text
CAMERA_OPERATOR
```

Permission:

```text
camera.read
```

Scope:

```text
Organization = Police Department
Geography = Ahmedabad District
```

Requested camera:

```text
CAM-001

Owner:
Police Department

Location:
Ahmedabad District
```

Evaluation:

```text
camera.read?
      |
     YES
      |
Organization matches?
      |
     YES
      |
Geography matches?
      |
     YES
      |
    ALLOW
```

## 11. Example Denial

Requested camera:

```text
CAM-900

Owner:
Transport Department

Location:
Ahmedabad District
```

User scope:

```text
Organization = Police Department
Geography = Ahmedabad District
```

Evaluation:

```text
camera.read?
      |
     YES
      |
Organization matches?
      |
      NO
      |
    DENY
```

Return:

```http
403 Forbidden
```

## 12. Geographic Scope Evaluation

Suppose a group has:

```text
Geographic Scope:
Ahmedabad District
```

Camera hierarchy:

```text
CAM-001
  ↓
Site Y
  ↓
Village X
  ↓
Daskroi
  ↓
Ahmedabad District
```

The camera is inside the district scope.

The authorization engine should resolve this through the geographic hierarchy rather than duplicating district/taluka/village IDs on every permission record.

## 13. Multiple Groups

A user can belong to multiple groups.

```text
Rahul
 |
 +--> Ahmedabad Police Camera Operator
 |
 +--> VMS Integration Team
```

Effective permissions are derived from all applicable groups.

```text
Group A Permissions
        +
Group B Permissions
        +
Group C Permissions
        =
Effective Permission Set
```

Each permission remains subject to its group's scope.

## 14. Example Multiple-Group Access

```text
Group A
Role:
CAMERA_OPERATOR

Scope:
Police + Ahmedabad


Group B
Role:
VMS_OPERATOR

Scope:
Police + VMS-001
```

The user can receive camera access within the first scope and VMS access within the second scope without creating a new role.

## 15. Avoid Role Explosion

Do not create:

```text
AhmedabadPoliceCameraOperator
SuratPoliceCameraOperator
AhmedabadTransportCameraOperator
AhmedabadPoliceInvestigator
```

Instead reuse:

```text
CAMERA_OPERATOR
INVESTIGATOR
VMS_OPERATOR
MAINTENANCE_OPERATOR
ANALYST
VIEWER
```

Custom roles are for a genuinely new *combination of responsibilities* — "shift supervisor" =
camera operation + alert acknowledgement + read-only VMS — not for a location. A responsibility
that already has a role, in a new place, is still an Access Group, never a new role.

and create reusable Access Groups:

```text
Ahmedabad Police Camera Operators
Surat Police Camera Operators
Ahmedabad Police Investigators
Ahmedabad Transport Operators
```

## 16. Model 3 Authorization

Model 3 has integration resources:

```text
User
 |
 v
Access Group
 |
 +--> Role
 |     |
 |     +--> vms.read
 |     +--> adapter.read
 |     +--> event.read
 |     +--> integration.manage
 |
 +--> Scope
       |
       +--> Organization
       +--> Geography
       +--> VMS
       +--> Adapter
```

Example:

```text
VMS Administrator
 |
 +--> Role: VMS_ADMIN
 |
 +--> Scope:
       Organization = Police
       VMS = VMS-001
```

The user can administer only permitted integration resources.

## 17. Resource-Level Authorization

The same model applies to VMS, adapters, events, observations, and alerts.

Example:

```text
VMS Operator Group
 |
 +--> Role: VMS_OPERATOR
 |
 +--> Organization: Police
 |
 +--> Resource: VMS-001
```

Request:

```http
GET /api/v1/vms/VMS-001/events
```

Decision:

```text
event.read
+
VMS-001 within scope
=
ALLOW
```

## 18. Event Authorization

Events should reference their source camera.

```text
Event EVT-001
       |
       v
Camera CAM-001
       |
       +--> Police Department
       +--> Ahmedabad District
```

A user with:

```text
event.read
Police Department
Ahmedabad District
```

can access the event.

## 19. Observation Authorization

An observation references its camera:

```text
Observation
    |
    v
Camera
    |
    +--> Organization
    +--> Site / Geography
```

Therefore:

```text
observation.read
+
camera within user's scope
=
ALLOW
```

## 20. Backend Enforcement

Authorization must happen on the backend.

Do not rely on frontend filtering:

```text
Frontend
  |
  +--> Hide button
  +--> Hide camera
```

Correct:

```text
Frontend
  |
  v
API
  |
  v
Authorization Service / Policy
  |
  v
Database Query
```

The database query should retrieve only resources the user is authorized to access where practical.

## 21. Authorization Service

Centralize authorization logic:

```text
AuthorizationService
--------------------

can(user, permission, resource)

Examples:

can(user, "camera.read", camera)
can(user, "event.read", event)
can(user, "vms.update", vms)
```

Return:

```text
ALLOW
DENY
```

## 22. Authorization Algorithm

Conceptual pseudocode:

```text
authorize(user, action, resource):

    groups = getUserGroups(user)

    for group in groups:

        role = getRole(group)
        permissions = getPermissions(role)

        if action not in permissions:
            continue

        scopes = getScopes(group)

        if resourceMatchesScopes(resource, scopes):
            return ALLOW

    return DENY
```

The production implementation should optimize permission and scope resolution rather than loading all records on every request.

## 23. Scope Matching

Scope matching depends on resource type.

For a Camera:

```text
Organization
+
Geography
```

For a VMS:

```text
Organization
+
Geography where applicable
+
Specific VMS
```

For an Event:

```text
Event
  ↓
Camera
  ↓
Organization + Geography
```

For an Observation:

```text
Observation
  ↓
Camera
  ↓
Organization + Geography
```

## 24. Audit Flow

Sensitive operations should be audited.

```text
User Request
     |
     v
Authentication
     |
     v
Authorization
     |
     v
Business Operation
     |
     v
Audit Log
```

Examples:

```text
Video accessed
Vehicle searched
Person search performed
Camera updated
VMS configuration changed
Adapter modified
Role changed
Group membership changed
Alert acknowledged
```

Audit logs must not contain plaintext credentials.

## 25. Group Lifecycle

Recommended lifecycle:

```text
DRAFT
  |
  v
ACTIVE
  |
  v
DISABLED
```

A disabled group grants no access.

## 26. User Group Membership

Recommended entity:

```text
user_groups
-----------

id
user_id
group_id
assigned_at
assigned_by
expires_at
status
```

Optional expiration supports temporary access:

```text
Investigation Team
Valid:
2026-08-20
to
2026-08-27
```

After expiry, the membership no longer grants access.

## 27. Access Group Structure

```text
access_groups
-------------
id
code
name
description
role_id
status
created_at
updated_at
created_by
updated_by
```

Scopes should be separately associated:

```text
group_scopes
------------
group_id
scope_id
```

This allows one group to have multiple scope dimensions.

## 28. Recommended Relationship Model

```text
USER
 |
 +----< USER_GROUPS >---- ACCESS_GROUP
                              |
                    +---------+---------+
                    |                   |
                    v                   v
                   ROLE              GROUP_SCOPES
                    |                   |
                    v                   v
             ROLE_PERMISSIONS        SCOPE
                    |                   |
                    v             +-----+------+
              PERMISSION          |            |
                              ORGANIZATION  GEOGRAPHY
```

Resources:

```text
CAMERA
├── organization_unit_id
└── site_id

VMS
├── organization_unit_id
└── optional site/geography

EVENT
└── camera_id

OBSERVATION
└── camera_id
```

## 29. Final Logical Flow

```text
                         USER
                           |
                           v
                    USER GROUP MEMBERSHIP
                           |
                           v
                     ACCESS GROUP
                       /                             /                              v           v
                   ROLE        SCOPES
                    |          / |                     v         /  |                PERMISSIONS   Org Geo Resource
                    \        |  |  /
                     \       |  | /
                      \      |  |/
                       v     v  v
                     RESOURCE
                         |
                         v
                AUTHORIZATION DECISION
                    /                               /                              ALLOW             DENY
                  |                 |
                  v                 v
          Execute operation      403
                  |
                  v
             Audit if required
```

## 30. Trinetra Authorization Principle

```text
USER
  ↓
ACCESS GROUP
  ↓
ROLE
  ↓
PERMISSIONS
  ↓
SCOPE
  ↓
RESOURCE
  ↓
ALLOW / DENY
```

Definitions:

```text
Role       = What can the user do?
Permission = Which exact action?
Scope      = Where / on which resources?
Group      = Reusable combination of Role + Scope
User       = Member of one or more Groups
Resource   = Camera / VMS / Event / Observation / Alert / etc.
```

This model is the common authorization foundation for Model 1, Model 2, and Model 3.
