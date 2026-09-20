-- v1.31: detection_snapshot records whether its image_data is gzip-compressed.
--
-- v1.30 stored evidence images resized and quality-reduced before storage. That traded pixel
-- fidelity for size; the corrected decision is the reverse — evidence stays exactly as captured
-- (full resolution, high JPEG quality), and lossless gzip is applied to the encoded bytes
-- instead (DetectionEndpoints, ai-worker/pipeline.py). image_data itself already holds whatever
-- bytes were given; this column just records which of the two they are, so GET
-- /detections/{id}/evidence knows whether to answer with Content-Encoding: gzip (letting the
-- browser decompress transparently) or serve the bytes as-is.

SET search_path = federation, public;

ALTER TABLE detection_snapshot ADD COLUMN IF NOT EXISTS content_encoding TEXT;

COMMENT ON COLUMN detection_snapshot.content_encoding IS
    'NULL for raw/uncompressed image_data, ''gzip'' when it is gzip-compressed — the evidence '
    'route sets Content-Encoding accordingly rather than decompressing server-side.';

-- v1.30's own table comment said images were "re-encoded server-side to a bounded size" —
-- corrected here: evidence is stored at full resolution/quality, size bounded only by lossless
-- gzip on the bytes (content_encoding above), not by resizing or quality reduction.
COMMENT ON TABLE detection_snapshot IS
    'One evidence image per detection, stored at full resolution/quality — see content_encoding '
    'for whether image_data is gzip-compressed. No FK to detection_event — same partitioning '
    'constraint detection_tag already documents.';
