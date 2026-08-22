# Model 2 --- Unified Viewing & Metadata Analytics

## Objective

Provide a unified interface for accessing available departmental video
sources and generate searchable metadata from accessible live or
recorded video without requiring replacement of existing departmental
VMS infrastructure.

## Processing Pipeline

``` text
VMS / Camera / Recording
          |
          v
       FFmpeg
          |
          v
 Frame Sampling / Decode
          |
          v
     GPU Inference
          |
     +----+----------------+
     |    |        |       |
    ANPR Vehicle  Person  Events
     |    |        |       |
     +----+--------+-------+
              |
              v
       Metadata Generation
              |
              v
       DB / Search Index
              |
              v
     Search / Alert / GIS
```

## Core Analytics

### ANPR

The system detects license plates, performs OCR, and creates structured
observations.

Example:

``` text
vehicle_number: GJ05AB1234
camera_id: C001
timestamp: 2026-08-19T10:32:15
confidence: 0.96
```

### Vehicle Detection & Tracking

A practical prototype stack can use:

-   YOLO for vehicle detection
-   ByteTrack or BoT-SORT for within-camera tracking
-   ANPR for vehicle-number association
-   Camera location + timestamp + direction for movement correlation

Continuous statewide vehicle identity should not be inferred from a
single tracker ID. Cross-camera association should use multiple signals
and confidence scoring.

### Person Detection & Search

For an authorised/controlled demonstration:

-   YOLO for person detection
-   ByteTrack for within-camera tracking
-   InsightFace for face detection/embeddings when a visible face is
    available
-   FAISS or another vector-search system for similarity search
-   Optional person Re-ID for cases where a face is unavailable

The system should present similarity as a confidence score rather than
claiming certainty. Production deployment requires appropriate legal,
privacy, access-control, retention, and audit controls.

## Metadata Schema

``` text
observation_id
camera_id
timestamp
object_type
object_track_id
vehicle_number
vehicle_type
person_reference_id
event_type
confidence
latitude
longitude
direction
snapshot_reference
video_reference
created_at
```

## Event Tagging

Events can be generated from detections or rules:

``` text
Detection
   |
   v
Rule / Analytics
   |
   +--> ANPR Event
   +--> Vehicle Event
   +--> Person Event
   +--> Configured Alert
```

## Search

Example queries:

``` text
Vehicle number + time range
Camera + time range
Event type + location
Person reference + time range
Vehicle of interest
Camera-wise observations
```

## Movement Reconstruction

A movement record is created from observations:

``` text
GJ05AB1234
    |
    +--> C001 | 10:32 | Location A
    |
    +--> C017 | 10:38 | Location B
    |
    +--> C031 | 10:44 | Location C
```

The GIS can plot these observations as a chronological path.

## Suggested Technology Stack

-   FFmpeg --- video decoding and processing
-   NVIDIA CUDA --- GPU execution
-   TensorRT --- optimized inference where supported
-   YOLO --- object detection
-   ByteTrack / BoT-SORT --- multi-object tracking
-   InsightFace --- controlled face matching use cases
-   FAISS / vector database --- embedding search
-   PostgreSQL --- structured metadata
-   PostGIS --- geographic queries
-   Search engine --- high-volume metadata search

## Demonstration

1.  Connect sample video.
2.  Decode with FFmpeg.
3.  Run GPU inference.
4.  Detect vehicles/persons.
5.  Generate ANPR and event metadata.
6.  Store metadata.
7.  Search by vehicle number/event/person reference.
8.  Display matched camera, timestamp and location.
9.  Correlate observations.
10. Visualize results on GIS.
