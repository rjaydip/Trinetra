"""RTSP capture via NVIDIA DeepStream — the GPU-accelerated path for a Jetson/dGPU deployment.

**Cannot be built or run on this development machine** (Apple Silicon, no NVIDIA GPU — DeepStream
is Linux + NVIDIA-GPU only) and so, unlike every other capture backend in this package, is
**not verified by execution here**. It is written to the portal's documented shape
(sentinel.gujarat.gov.in/resource: "Use nvurisrcbin / uridecodebin with the RTSP URI and set
select-rtp-protocol=4 (TCP)") and checked structurally (the pipeline element graph mirrors that
guidance, `select-rtp-protocol=4` is TCP per DeepStream/GStreamer's own `GstRTSPLowerTrans`
enum), but its correctness has not been confirmed against a running DeepStream install. Treat
it as a documented starting point for whoever deploys onto real GPU hardware, not as validated.

`available()` fails fast and specifically — checking for the actual `nvurisrcbin` GStreamer
element — rather than letting construction fail deep inside a pipeline string on a platform
that was never going to support it.

DeepStream pipelines are themselves GStreamer pipelines (using NVIDIA's proprietary elements),
so this reuses `gstreamer_source.py`'s appsink/PTS-anchoring machinery rather than duplicating
it — only the source/decode portion of the pipeline differs.
"""

from __future__ import annotations

import logging

from capture.gstreamer_source import GStreamerFrameSource, gstreamer_available

logger = logging.getLogger(__name__)

_REQUIRED_ELEMENTS = ("nvurisrcbin", "nvvideoconvert", "nvstreammux")


def deepstream_available() -> bool:
    """True only on a real DeepStream install: GStreamer plus NVIDIA's plugins registered."""
    if not gstreamer_available():
        return False

    import gi

    gi.require_version("Gst", "1.0")
    from gi.repository import Gst

    if not Gst.is_initialized():
        Gst.init(None)

    registry = Gst.Registry.get()
    return all(registry.find_plugin(name) is not None or Gst.ElementFactory.find(name) is not None
               for name in _REQUIRED_ELEMENTS)


class DeepStreamFrameSource(GStreamerFrameSource):
    """Same appsink/PTS/reconnect behaviour as `GStreamerFrameSource`, with `nvurisrcbin`
    (hardware-accelerated decode) in place of `rtspsrc ! decodebin`.

    `select-rtp-protocol=4` is DeepStream/GStreamer's `GstRTSPLowerTrans` enum value for TCP —
    the numeric form is what `nvurisrcbin`'s property expects; there is no string alias.
    """

    def __init__(self, rtsp_url: str, processing_fps: float, **kwargs: object) -> None:
        if not deepstream_available():
            raise RuntimeError(
                "NVIDIA DeepStream plugins (nvurisrcbin/nvvideoconvert/nvstreammux) were not "
                "found. DeepStream requires a Linux host with an NVIDIA GPU and the DeepStream "
                "SDK installed — it cannot run on this machine. This class is written to the "
                "Sentinel portal's documented pipeline shape but has not been executed against "
                "a real DeepStream install; verify it on target hardware before relying on it."
            )
        super().__init__(rtsp_url, processing_fps, **kwargs)

    def _pipeline_string(self) -> str:
        return (
            f"nvurisrcbin uri={self._rtsp_url} select-rtp-protocol=4 "
            "! nvvideoconvert ! video/x-raw,format=BGR "
            "! appsink name=sink emit-signals=false sync=false max-buffers=1 drop=true"
        )
