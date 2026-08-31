"""The worker's monitoring HTTP surface: `/health` (liveness) and `/metrics` (Prometheus).

Deliberately its own tiny FastAPI app, independent of whatever drives the capture/inference
loop (`worker.py`), so metrics stay servable even if a particular camera's processing loop is
stuck — the two run as separate threads within the same process.
"""

from __future__ import annotations

from fastapi import FastAPI, Response
from prometheus_client import CONTENT_TYPE_LATEST, generate_latest

app = FastAPI(title="Trinetra AI Worker")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.get("/metrics")
def metrics() -> Response:
    return Response(content=generate_latest(), media_type=CONTENT_TYPE_LATEST)
