# Model 3 --- VMS Federation & Middleware Integration

## Objective

Provide a vendor-neutral interoperability layer that connects
heterogeneous departmental VMS platforms through adapters/plugins and
exposes common camera, metadata, event, alert, and workflow interfaces.

## Why Middleware

Departmental VMS products may expose different APIs, SDKs, event
formats, authentication mechanisms, and capabilities.

Instead of embedding vendor-specific logic throughout the application,
the platform isolates it inside adapters.

## Adapter Architecture

``` text
Vendor A VMS ----> Vendor A Adapter --\
Vendor B VMS ----> Vendor B Adapter ---+--> Integration Layer
ONVIF System ----> ONVIF Adapter ------/
Future Vendor ---> New Adapter --------/
```

## Common Adapter Contract

Conceptual capabilities:

``` text
get_cameras()
get_camera_status()
get_streams()
get_recordings()
get_events()
get_metadata()
subscribe_events()
```

Not every VMS will support every capability. Adapters should expose
capability discovery so downstream services know what each integration
supports.

## Metadata & Event Exchange

``` text
VMS Adapters
     |
     v
Metadata / Event Bus
     |
     +--> Analytics
     +--> Search Index
     +--> Alert Engine
     +--> Correlation Engine
     +--> GIS
     +--> Dashboard
```

An event bus decouples producers and consumers and allows new services
to subscribe without modifying existing VMS adapters.

## Event Normalisation

Vendor-specific events should be mapped into a common schema:

``` text
event_id
source_vms
camera_id
organization_unit_id
event_type
timestamp
severity
location
object_reference
confidence
source_event_id
raw_reference
```

## Cross-System Correlation

Example:

``` text
Department A
Camera A001
GJ05AB1234
10:30
      |
      v
Correlation Engine
      |
      v
Department B
Camera B045
GJ05AB1234
10:38
      |
      v
Department C
Camera C101
GJ05AB1234
10:46
```

The engine can correlate observations using:

-   Vehicle number
-   Person reference / similarity result
-   Timestamp
-   Camera location
-   Direction
-   Event type
-   Confidence
-   Configurable business rules

## Unified Workflow

The middleware enables a common operational workflow:

``` text
Event
  |
  v
Normalisation
  |
  v
Correlation
  |
  +--> Alert
  +--> Case / Workflow
  +--> Dashboard
  +--> Search
  +--> GIS
```

## Camera Registration and Background Discovery

Model 3 registers a VMS target, not individual cameras. The VMS is the source of truth for the
inventory it exposes; the worker mirrors that inventory into `federation.federated_camera`.

### Onboarding a VMS target

``` text
POST /api/v1/vms
        |
        v
PUT /api/v1/vms/{id}/credential
        |
        v
POST /api/v1/vms/{id}/test
        |
        v
POST /api/v1/vms/{id}/state  { "state": "Active" }
```

The state transition to `Active` makes the target eligible for worker leasing. The API does not
run the inventory poll itself. Start the worker process separately:

``` bash
dotnet run --project src/Trinetra.Federation.Worker
```

The credential set by `PUT /api/v1/vms/{id}/credential` is write-only through the API; the
Model 3 connector worker resolves it in-process at connect time. The one exception is Model 2's
AI worker, which connects to camera RTSP streams directly and reads the credential over HTTP:

``` text
GET /api/v1/vms/{id}/credential/resolve   -> { credentialReference, username, password, token }
```

`credential.resolve` permission, carried only by the `DETECTION_WORKER` machine role. Scoped
through the target like every other `/vms/{id}` route — the worker's access group must cover
the targets or the call returns 404. Every resolution is written to `credential_access_log`;
if that write fails the response is 503 and the secret is withheld. Vendor extras (Milestone
OAuth client, Genetec application id) are not returned by this route.

### Worker discovery lifecycle

1. `LeaseManager` claims active targets so exactly one worker owns a target at a time.
2. `ConnectorSupervisor` starts one `TargetWorker` for each claimed target.
3. `TargetWorker` creates the vendor adapter, connects, and probes capabilities.
4. The inventory loop calls the adapter's bulk `GetCamerasAsync()` method every five minutes by
   default. The first call occurs after the first inventory interval.
5. The returned collection is upserted using `(target_id, native_camera_id)` as the key.
6. The status loop refreshes enabled, recording, and health state every 30 seconds by default.
7. The event loop subscribes to events or polls them, according to the adapter capability.
8. If the lease is lost, all loops are cancelled immediately and polling stops.

The inventory loop deliberately does not delete a camera when a VMS temporarily returns a
partial list. Reconciliation of genuine removals is a separate operation.

### Registry boundary

Model 3 has no camera-creation endpoint of its own. `federated_camera` is the VMS-discovered
inventory, keyed by `(target_id, native_camera_id)` — not the authoritative camera registry.
The registry (`cameras`) and its write side (`POST /api/v1/cameras`, `/bulk-import`,
`/{id}/reconcile`, `/from-federated`) belong to Model 1 (`db/versions/v1.6.sql`,
`MODEL-1-REGISTRY-GIS.md`). A `federated_camera` row's `camera_id` stays `NULL` until a
reconciliation call links it to a registry record.

``` text
Model 1 camera registry (authoritative GIS/business record)
             |
             | reconciliation
             v
federated_camera.camera_id  <-  (target_id, native_camera_id)
```

Model 1 owns camera identity, location, orientation, coverage, and maintenance metadata. Model 3
owns vendor inventory, adapter state, stream references, health, and event integration. This
separation prevents a transient VMS inventory response from overwriting authoritative registry
data.

## Boundary with Model 2 Video Analytics

Model 3 does **not** capture, decode, transcode, or continuously forward raw video. It provides
Model 2 with the information required to access the source:

``` text
Model 3
  ├── camera inventory
  ├── RTSP/HTTP stream references
  ├── camera/VMS status
  └── VMS-generated events
             |
             v
Model 2
  ├── stream capture
  ├── FFmpeg decoding and frame sampling
  ├── GPU inference
  └── ANPR, vehicle, person and rule-based analytics
```

Model 2 may obtain stream references through the Model 3 API or an approved internal integration
contract. Model 3 retains responsibility for VMS authentication and inventory health; Model 2
owns the media session, decoding, inference and generated observation metadata.

Raw video should normally remain on the VMS or an approved video/object-storage layer. Kafka is
intended for normalized events and analytics metadata, not an unrestricted full-resolution video
transport. The current repository implements the Model 3 side of this boundary; the Model 2
capture/inference service and the Kafka producer/consumer pipeline are not implemented yet.

## Future Connector Framework

A new vendor should require:

1.  Implementing the adapter contract.
2.  Registering supported capabilities.
3.  Mapping vendor events to the common event schema.
4.  Configuring authentication and endpoint details.
5.  Running connector validation tests.

The core dashboard and analytics services should not need
vendor-specific changes.

## Security

Recommended controls:

-   RBAC
-   Adapter-specific credentials
-   Secure secret references
-   TLS for network communication where supported
-   Audit logging
-   Connector health monitoring
-   Least-privilege integration accounts
-   Network segmentation
-   Rate limiting and API validation

## Demonstration

1.  Connect two simulated/different VMS systems.
2.  Configure their adapters.
3.  Pull camera and status metadata.
4.  Receive or generate events.
5.  Normalise events into the common schema.
6.  Publish events to the exchange layer.
7.  Run cross-system correlation.
8.  Display the result in the unified dashboard.
9.  Trigger an alert/workflow.
