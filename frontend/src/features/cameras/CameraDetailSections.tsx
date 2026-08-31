import type { CameraResponse } from '../../api/models';
import { StatusBadge } from '../../components/ui';

function tone(status: string): 'success' | 'warning' | 'danger' | 'neutral' {
  const normal = status.toUpperCase();
  if (['ACTIVE', 'ONLINE', 'CURRENT'].includes(normal)) return 'success';
  if (['OFFLINE', 'FAILED', 'RETIRED'].includes(normal)) return 'danger';
  if (['DEGRADED', 'MAINTENANCE', 'DUE'].includes(normal)) return 'warning';
  return 'neutral';
}

export function CameraDetailSections({ camera }: { camera: CameraResponse }) {
  return (
    <div className="camera-detail-sections">
      <section aria-labelledby="camera-status-heading">
        <h3 id="camera-status-heading">Status</h3>
        <dl>
          <div><dt>Operational</dt><dd><StatusBadge tone={tone(camera.operationalStatus)}>{camera.operationalStatus}</StatusBadge></dd></div>
          <div><dt>Connectivity</dt><dd><StatusBadge tone={tone(camera.connectivityStatus)}>{camera.connectivityStatus}</StatusBadge></dd></div>
          <div><dt>Maintenance</dt><dd><StatusBadge tone={tone(camera.maintenanceStatus)}>{camera.maintenanceStatus}</StatusBadge></dd></div>
        </dl>
      </section>
      <section aria-labelledby="camera-location-heading">
        <h3 id="camera-location-heading">Registry details</h3>
        <dl>
          <div><dt>Camera code</dt><dd>{camera.cameraCode}</dd></div>
          <div><dt>Type</dt><dd>{camera.cameraType}</dd></div>
          <div><dt>Coordinates</dt><dd>{camera.latitude}, {camera.longitude}</dd></div>
          <div><dt>Manufacturer</dt><dd>{camera.manufacturer ?? 'Not recorded'}</dd></div>
          <div><dt>Last seen</dt><dd>{camera.lastSeenAt ? new Date(camera.lastSeenAt).toLocaleString() : 'Not recorded'}</dd></div>
        </dl>
      </section>
    </div>
  );
}
