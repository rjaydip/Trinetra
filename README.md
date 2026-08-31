# Trinetra CCTV Intelligence & Integration Platform

## Overview

Trinetra is a hybrid CCTV intelligence and interoperability platform
combining three capabilities:

-   **Model 1 --- Centralised CCTV Registry & GIS Mapping**
-   **Model 2 --- Unified Viewing & Metadata Analytics**
-   **Model 3 --- VMS Federation & Middleware Integration**

The platform is designed to create a common intelligence and integration
layer over existing departmental CCTV infrastructure. It does not
require immediate replacement of departmental cameras or VMS platforms.

## Problem

CCTV assets are deployed independently by multiple departments and
institutions using heterogeneous vendors, VMS platforms, protocols,
storage systems, and operational processes. This creates fragmented
asset visibility, difficult geographic coverage assessment, limited
searchable video intelligence, and interoperability challenges.

## Solution

The platform combines:

1.  **Asset Intelligence:** a central camera registry with GIS location,
    coverage, health, maintenance, ownership, and technical metadata.
2.  **Video Intelligence:** processing of accessible live or recorded
    video to generate structured metadata such as ANPR observations,
    person/vehicle detections, and events.
3.  **Integration Intelligence:** vendor-neutral adapters,
    metadata/event exchange, and cross-system event correlation.

## High-Level Architecture

``` text
                    ┌───────────────────────────┐
                    │       User Interfaces      │
                    │ GIS | Search | Video |     │
                    │ Events | Alerts | Reports  │
                    └─────────────┬─────────────┘
                                  │
                    ┌─────────────▼─────────────┐
                    │       API / Services       │
                    └─────────────┬─────────────┘
                                  │
          ┌───────────────────────┼───────────────────────┐
          │                       │                       │
┌─────────▼─────────┐   ┌────────▼────────┐   ┌─────────▼─────────┐
│ MODEL 1            │   │ MODEL 2         │   │ MODEL 3            │
│ Registry + GIS     │   │ Video Analytics │   │ VMS Federation     │
│                    │   │ + Metadata      │   │ + Middleware       │
└─────────┬─────────┘   └────────┬────────┘   └─────────┬─────────┘
          │                      │                      │
          └──────────────────────┼──────────────────────┘
                                 │
                    ┌────────────▼────────────┐
                    │ Metadata / Event Layer  │
                    │ DB + Search + Event Bus │
                    └─────────────────────────┘
```

## Key Demonstration

The hackathon prototype should demonstrate an end-to-end scenario:

**Register camera → place camera on GIS → show coverage → connect sample
VMS/feed → process video → generate ANPR/person/vehicle/event metadata →
search metadata → correlate observations → show movement/event
information on GIS → generate alert.**

## Running it

Model 3 is implemented. Model 1 (Registry & GIS) has a first slice implemented — the camera
registry, its CRUD / bulk-import / health / maintenance / reconciliation endpoints, and a
GeoJSON map source with estimated coverage sectors (`docs/MODEL-1-API-PLAN.md`); coverage-gap
analysis is still specification. Model 2's capture-and-inference worker
(`ai-worker/`, Python — see `ai-worker/README.md`) exists standalone, discovering cameras from
Model 3's registry and running real vehicle/plate/OCR inference; its integration into the
platform's event/metadata layer (ingest endpoint, watchlist, alerts) is still specification.

The database provider is deployment-specific and may change over time. The current development
environment uses Supabase-hosted PostgreSQL. Supabase is not a permanent architectural
requirement; configure the selected PostgreSQL provider through
`ConnectionStrings:Federation` (or the `ConnectionStrings__Federation` environment variable).

``` bash
# Optional local PostgreSQL fallback; skip this when using Supabase or another provider.
docker compose up -d
psql -h your-database-host -U your-database-user -d trinetra -f db/versions/v1.sql
# Then configure config/trinetra.settings.json or ConnectionStrings__Federation.
dotnet run --project src/Trinetra.Federation.Api       # HTTP API
dotnet run --project src/Trinetra.Federation.Worker    # connector workers
```

In the Development environment, the interactive Scalar API Reference is available at
`/scalar`; the generated OpenAPI document is at `/openapi/v1.json`.

The Model 1 registry frontend lives in [`frontend/`](frontend/README.md). From that directory,
copy `.env.example`, run `npm install`, and start it with `npm run dev -- --host 0.0.0.0`. It uses
the deployed API (default `http://192.168.1.16:5261`); configure the API's explicit
`Auth:AllowedOrigins` entry for the frontend origin before signing in.

Nothing in the running system creates, changes or checks the schema — that is entirely yours.
`db/versions/v1.sql` builds a complete database; a later version adds its own file. To change the
schema, write the next version and apply it yourself. `docs/OPERATIONS.md` covers configuration,
upgrades and troubleshooting.

## Repository Structure

``` text
README.md
CLAUDE.md                              working notes for Claude Code

docs/
  ARCHITECTURE-MODEL-3.md              production design and its trade-offs
  AUTHORIZATION.md                     how RBAC is enforced, and what is deliberately denied
  OPERATIONS.md                        setup, configuration, upgrades, troubleshooting
  DEPLOYMENT.md                        bare-metal topology, systemd, container image, rolling upgrades

  MODEL-1-REGISTRY-GIS.md              specification — first slice implemented
  MODEL-1-API-PLAN.md                  camera registry + GIS API contract and build status
  MODEL-2-VIDEO-METADATA-ANALYTICS.md  specification
  MODEL-3-VMS-FEDERATION-MIDDLEWARE.md specification — implemented
  TECHNICAL-DESIGN.md                  integration view across the three models

  DEPARTMENT-SCHEMA.md                 organization hierarchy
  GEOGRAPHY-SCHEMA.md                  operator-defined location hierarchy
  CAMERA-SCHEMA.md                     camera registry
  RBAC-LOGICAL-FLOW.md                 the authorization model

db/
  versions/v1.sql                      the complete database — apply to an empty PostgreSQL
  objects/                             one file per table / function / view, for review

Dockerfile                             container image for the API (alternative to systemd)
deploy/systemd/                        unit files and environment templates
config/trinetra.settings.example.json  config template — copy to trinetra.settings.json

src/                                   Core, Adapters, Runtime, Bus, Storage, Api, Worker
tools/                                 admin CLI, VMS simulator
tests/                                 unit, integration, load

ai-worker/                             Model 2 capture + AI inference worker (Python) — see
                                        ai-worker/README.md
```

## Design Principles

-   Existing departmental infrastructure remains operational.
-   Vendor-specific integration is isolated behind adapters.
-   Video is treated as the source and metadata as the searchable
    intelligence layer.
-   Sensitive credentials are referenced through secure secret
    management rather than ordinary metadata fields.
-   Analytics components are modular and replaceable.
-   Cross-camera matching is treated as probabilistic when using AI
    similarity.
-   All sensitive operations should be protected with RBAC and audit
    logging.
-   The architecture should support future departments, vendors,
    cameras, analytics models, and integrations.

## Prototype Scope

The initial prototype can use a controlled set of sample cameras, VMS
integrations, recorded/live video, and test subjects. Production-scale
capacity, security hardening, high availability, and large-scale
performance should be validated separately.

## Expected Benefits

-   Centralised visibility of CCTV assets
-   Faster camera discovery and maintenance management
-   GIS-based coverage-gap identification
-   Searchable ANPR and event metadata
-   Faster video investigation
-   Cross-camera and cross-department correlation
-   Vendor-neutral future integration
-   Preservation of existing departmental investments
