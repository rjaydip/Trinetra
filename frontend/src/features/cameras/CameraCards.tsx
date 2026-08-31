import { Link } from 'react-router-dom';

import type { CameraResponse } from '../../api/models';
import { StatusBadge } from '../../components/ui';

function tone(status: string): 'success' | 'warning' | 'danger' | 'neutral' {
  const value = status.toUpperCase();
  if (['ACTIVE', 'ONLINE', 'NORMAL', 'CURRENT'].includes(value)) return 'success';
  if (['OFFLINE', 'FAILED', 'RETIRED'].includes(value)) return 'danger';
  if (['DEGRADED', 'MAINTENANCE', 'UNDER_MAINTENANCE', 'DUE'].includes(value)) return 'warning';
  return 'neutral';
}

export function CameraCards({ cameras }: { cameras: CameraResponse[] }) {
  return (
    <ul className="camera-cards" aria-label="Camera registry results">
      {cameras.map((camera) => <li className="camera-card" key={camera.id}>
        <div><p className="eyebrow">{camera.cameraCode}</p><h2><Link to={`/cameras/${camera.id}`}>{camera.name}</Link></h2></div>
        <p>{camera.cameraType}</p>
        <dl>
          <div><dt>Operational</dt><dd><StatusBadge tone={tone(camera.operationalStatus)}>{camera.operationalStatus}</StatusBadge></dd></div>
          <div><dt>Connectivity</dt><dd><StatusBadge tone={tone(camera.connectivityStatus)}>{camera.connectivityStatus}</StatusBadge></dd></div>
          <div><dt>Maintenance</dt><dd><StatusBadge tone={tone(camera.maintenanceStatus)}>{camera.maintenanceStatus}</StatusBadge></dd></div>
        </dl>
      </li>)}
    </ul>
  );
}
