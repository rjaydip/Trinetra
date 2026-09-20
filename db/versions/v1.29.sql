-- v1.29: DETECTION_WORKER can resolve a standalone registry camera's own credential.
--
-- v1.27's camera-lease claim endpoint hands a worker registry cameras (federation.cameras),
-- not only VMS-federated ones. DETECTION_WORKER already holds credential.resolve for a VMS
-- target's login (GET /vms/{id}/credential/resolve); a registry camera's own login is a
-- different route (GET /cameras/{id}/credential/resolve, camera.credential.resolve) that until
-- now only STREAMING_GATEWAY held. Without this a worker can discover and claim a registry
-- camera but can never learn the RTSP login it needs to actually open the stream.

SET search_path = federation, public;

INSERT INTO role_permissions (role_id, permission_code)
SELECT r.id, p.code FROM roles r JOIN permissions p ON TRUE
WHERE (r.code, p.code) IN (('DETECTION_WORKER', 'camera.credential.resolve'))
ON CONFLICT DO NOTHING;
