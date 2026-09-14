-- ===========================================================================
-- v1.25 — native HLS/WebRTC stream sources, bypassing MediaMTX's RTSP remux
-- ===========================================================================
--
-- The streaming gateway (docs/STREAMING-GATEWAY-PLAN.md) has so far assumed every camera is
-- RTSP-only and needs MediaMTX to pull it and remux to HLS. Some cameras/NVRs expose their own
-- native HLS and/or WebRTC (WHEP) endpoints directly — for those, pulling their RTSP feed into
-- MediaMTX just to re-serve HLS is unnecessary work the device has already done itself. This
-- lets a camera opt into pointing the gateway at its own native URL instead, per-protocol:
--
--   RTSP    (default) — unchanged: MediaMTX pulls stream_reference, Federation.Api proxies its
--            HLS output, exactly as today. The only mode every existing camera has.
--   HLS     — Federation.Api proxies native_hls_url directly; MediaMTX is never involved for
--             this camera. For dashboards, mobile, restricted networks.
--   WEBRTC  — Federation.Api proxies only the WHEP *signaling* exchange (the SDP offer/answer
--             POST, and the session-teardown DELETE) to native_webrtc_url; the actual media
--             then flows directly between the viewer's browser and the device over WebRTC,
--             never through this API. For low-latency browser preview.
--
-- RTSP itself stays available unconditionally regardless of this preference — an AI-inference
-- consumer (OpenCV/GStreamer/FFmpeg/DeepStream) connects to stream_reference directly and never
-- goes through this gateway or cares what a browser viewer's preference is.
--
-- Requires: v1.sql .. v1.24.sql
-- ===========================================================================

SET search_path = federation, public;

ALTER TABLE cameras
    ADD COLUMN IF NOT EXISTS stream_preference VARCHAR(20) NOT NULL DEFAULT 'RTSP'
        CHECK (stream_preference IN ('RTSP', 'HLS', 'WEBRTC')),
    ADD COLUMN IF NOT EXISTS native_hls_url TEXT,
    ADD COLUMN IF NOT EXISTS native_webrtc_url TEXT;
