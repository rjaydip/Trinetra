# Technical Design --- Integrated Models 1, 2 and 3

## 1. Architecture Goal

Create a modular platform where CCTV asset information, video-derived
metadata, and heterogeneous VMS integrations are connected through
common APIs and event interfaces.

## 2. Logical Architecture

``` text
                    Users
                      |
          +-----------+-----------+
          | Web / GIS / Operations|
          +-----------+-----------+
                      |
                API Gateway
                      |
     +----------------+----------------+
     |                |                |
 Registry         Analytics        Federation
 Services         Services         Services
     |                |                |
     |           FFmpeg/GPU           |
     |                |                |
     |          AI Models             |
     |                |                |
     +----------------+----------------+
                      |
             Metadata / Event Bus
                      |
       +--------------+---------------+
       |              |               |
   PostgreSQL       Search         Object Store
    + PostGIS       Index           References
```

## 3. Data Flow

### Camera Registration

``` text
Bulk / Manual / API
        |
        v
Validation
        |
        v
Camera Registry
        |
        v
PostGIS
        |
        v
GIS Map / Coverage
```

### Video Analytics

``` text
VMS / Recording
      |
      v
Adapter / Source
      |
      v
FFmpeg
      |
      v
Frame Sampling
      |
      v
GPU Inference
      |
      v
Metadata
      |
      v
Event Bus + Search + DB
```

### Cross-System Correlation

``` text
Adapter A ----\
Adapter B -----+--> Event Bus --> Correlation Engine
Adapter C ----/                         |
                                        v
                              Unified Event / Alert
```

## 4. Suggested Technology Choices

### Backend

A suitable implementation can use a REST API framework such as FastAPI,
Node.js, Java Spring Boot, or .NET depending on the development team's
expertise.

### Database

**PostgreSQL + PostGIS** for:

-   Camera registry
-   Departments
-   Locations
-   Camera coverage
-   Events
-   Maintenance
-   Geographic queries

### Search

OpenSearch or Elasticsearch can be used for high-volume metadata and
event search.

### Event Bus

Kafka can provide asynchronous metadata/event exchange between adapters,
analytics, correlation, and dashboard services.

### Video

FFmpeg can handle decoding, transcoding, frame extraction, and
compatible stream processing.

### AI

Prototype options:

-   YOLO for object detection
-   ByteTrack / BoT-SORT for tracking
-   ANPR pipeline with plate detection + OCR
-   InsightFace for controlled, authorised face matching use cases
-   Optional person/vehicle Re-ID for cross-camera similarity

### GIS

A web GIS frontend can render camera points, coverage sectors, routes,
and gaps using a map library. PostGIS provides the spatial data layer.

## 5. API Design

Example API groups:

``` text
/api/auth
/api/departments
/api/cameras
/api/cameras/{id}/health
/api/cameras/{id}/maintenance
/api/gis
/api/vms
/api/connectors
/api/streams
/api/analytics
/api/observations
/api/events
/api/alerts
/api/search
/api/audit
```

## 6. Common Event Contract

``` json
{
  "event_id": "EVT-001",
  "source": "VMS-A",
  "camera_id": "CAM-001",
  "department_id": "DEPT-001",
  "timestamp": "2026-08-19T10:32:15Z",
  "event_type": "ANPR_DETECTION",
  "object_reference": "GJ05AB1234",
  "confidence": 0.96,
  "location": {
    "latitude": 21.1702,
    "longitude": 72.8311
  }
}
```

## 7. Security Architecture

-   RBAC for all user functions
-   Department-scoped access where required
-   Secure credential/secret references
-   Encryption in transit
-   Encryption at rest where applicable
-   API authentication and authorisation
-   Audit trails
-   Connector isolation
-   Network segmentation
-   Least-privilege service accounts
-   Configurable data retention

## 8. Production Boundary

This section originally scoped the work as a hackathon prototype demonstrated on controlled
sample feeds. **That boundary has been superseded by a decision of the project owner: Model 3 is
built production-ready from day one.** The items below are therefore work to complete, not
validation to defer — and the table records honestly where each stands today.

| Concern | Status |
|---|---|
| Capacity | Designed for 80,000 cameras; the constraint is VMS instances, not cameras. Validated against the simulator, **not yet against a real estate** |
| Reliability | Lease-based work assignment, circuit breaking, at-least-once delivery with idempotent sinks |
| Cybersecurity | JWT and API-key authentication, group-based RBAC scoped by organization and geography, application-side AES-256-GCM credential sealing, full audit trail. **No mTLS between services yet** |
| Privacy and legal compliance | **Outstanding.** Retention is configurable and enforced, but the periods are a policy decision nobody has yet confirmed against statutory obligation |
| Model accuracy | Model 2 is unbuilt. Cross-camera identity remains probabilistic by design — see below |
| Retention policies | Implemented: partition-based, irreversible, guarded against misconfiguration. The *values* still need a policy owner |
| Disaster recovery | **Outstanding.** Assumes PostgreSQL replication and a tested restore; neither is set up here |
| High availability | API instances are stateless and interchangeable; the worker fleet self-rebalances. The **database is a single point of failure** until replication exists |
| Large-scale network/storage | Sized in `ARCHITECTURE-MODEL-3.md` §2. Not yet exercised at scale |

Two constraints from the original scoping survive unchanged, because they are about honesty
rather than readiness:

- **Cross-camera identity is probabilistic.** A tracker ID is valid only within one camera. Any
  cross-camera association carries a confidence score and must never be surfaced as certainty in
  an API response or a UI string.
- **Coverage is estimated, not measured.** Sectors are derived from azimuth, field of view and
  effective range. Terrain and obstructions are not modelled, so coverage is a planning aid.

Implementation status for Model 3 is in `ARCHITECTURE-MODEL-3.md`; what is deliberately not built
is listed there under Deliberate non-goals.

## 9. Suggested Development Order

### Phase 1

Build Model 1 registry and GIS.

### Phase 2

Add camera health and maintenance.

### Phase 3

Build Model 2 video-processing pipeline.

### Phase 4

Add ANPR and searchable metadata.

### Phase 5

Add person/vehicle event tracking for the controlled demo.

### Phase 6

Build two VMS adapters for Model 3.

### Phase 7

Add event bus and common event schema.

### Phase 8

Implement cross-system correlation.

### Phase 9

Connect correlation results to GIS and unified alerts.

### Phase 10

Harden security, audit logging, testing, and demonstration workflows.
