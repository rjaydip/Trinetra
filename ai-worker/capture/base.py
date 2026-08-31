"""Frame-source contract: anything that can hand the pipeline timestamped frames.

`RtspFrameSource` (live cameras) and `StaticFrameSource`/`SampleVideoFrameSource` (validation
without a live feed) both implement this, so `pipeline.py` and `worker.py` never know which
kind of source they're reading from.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Iterator
from dataclasses import dataclass
from datetime import datetime

import numpy as np


@dataclass(frozen=True)
class Frame:
    captured_at: datetime
    image: np.ndarray


class FrameSource(ABC):
    @abstractmethod
    def frames(self) -> Iterator[Frame]:
        """Yields frames already sampled down to the configured processing FPS."""
        ...

    @abstractmethod
    def close(self) -> None:
        ...

    def __enter__(self) -> "FrameSource":
        return self

    def __exit__(self, *_exc_info: object) -> None:
        self.close()
