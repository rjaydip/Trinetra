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
department_id
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
