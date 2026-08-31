"""Abstract contracts for each pipeline stage.

`pipeline.py` depends only on these three ABCs, never on a concrete model or HTTP client. Each
stage has independent local/remote(/heuristic) implementations selected by `config.py` at
startup (see `registry.py`) — adding a new model or a new hosted API is a new class plus one
registry entry, not a change to the pipeline or to the other stages.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass

import numpy as np


@dataclass(frozen=True)
class BoundingBox:
    """Pixel-space box in the frame/crop it was detected against."""

    x1: int
    y1: int
    x2: int
    y2: int

    def crop(self, image: np.ndarray) -> np.ndarray:
        h, w = image.shape[:2]
        x1, y1 = max(0, self.x1), max(0, self.y1)
        x2, y2 = min(w, self.x2), min(h, self.y2)
        return image[y1:y2, x1:x2]


@dataclass(frozen=True)
class VehicleDetection:
    bbox: BoundingBox
    vehicle_type: str  # car | truck | bus | motorcycle | vehicle
    confidence: float


@dataclass(frozen=True)
class PlateDetection:
    bbox: BoundingBox
    confidence: float


@dataclass(frozen=True)
class AnprResult:
    text: str
    confidence: float


class VehicleDetector(ABC):
    """Stage 1: find vehicles in a full frame."""

    @abstractmethod
    def detect(self, frame: np.ndarray) -> list[VehicleDetection]:
        ...


class PlateDetector(ABC):
    """Stage 2: find license-plate regions within a vehicle crop."""

    @abstractmethod
    def detect(self, vehicle_crop: np.ndarray) -> list[PlateDetection]:
        ...


class AnprProcessor(ABC):
    """Stage 3: read the plate text out of a plate crop."""

    @abstractmethod
    def read(self, plate_crop: np.ndarray) -> AnprResult | None:
        ...
