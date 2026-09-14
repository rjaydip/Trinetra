export const cameraTypes = ['FIXED', 'PTZ', 'DOME', 'BULLET', 'ANPR', 'THERMAL', 'MULTISENSOR', 'OTHER'] as const;
export const protocols = ['RTSP', 'RTSPS', 'ONVIF', 'HTTP', 'HTTPS', 'RTMP', 'SRT', 'OTHER'] as const;
export const operationalStatuses = ['ONLINE', 'OFFLINE', 'DEGRADED', 'UNKNOWN'] as const;
export const connectivityStatuses = ['CONNECTED', 'DISCONNECTED', 'UNKNOWN'] as const;
export const maintenanceStatuses = ['NORMAL', 'REQUIRED', 'UNDER_MAINTENANCE'] as const;
/** How this camera's live feed is served (v1.25) — `RTSP` pulls through the MediaMTX gateway
 * (remuxed from the camera's RTSP source); `HLS`/`WEBRTC` proxy the camera's own native URL
 * directly, no remuxing. */
export const streamPreferences = ['RTSP', 'HLS', 'WEBRTC'] as const;

export const roundCoordinate = (value: number) => Number(value.toFixed(7));
