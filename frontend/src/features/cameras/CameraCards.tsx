import { Link } from 'react-router-dom';

import type { CameraResponse } from '../../api/models';
import { StatusBadge } from '../../components/ui';
import { downSince, EMPHASIZE_AFTER_HOURS } from './downSince';
import { statusTone } from './statusTone';

export function CameraCards({ cameras }: { cameras: CameraResponse[] }) {
  return (
    <ul className="camera-cards" aria-label="Camera registry results">
      {cameras.map((camera) => {
        const connectivityTone = statusTone(camera.connectivityStatus);
        const { hours } = downSince(camera.lastSeenAt);
        return <li className="camera-card" key={camera.id}>
          <div><p className="eyebrow">{camera.cameraCode}</p><h2><Link to={`/cameras/${camera.id}`}>{camera.name}</Link></h2></div>
          <p>{camera.cameraType}</p>
          <dl>
            <div><dt>Operational</dt><dd><StatusBadge tone={statusTone(camera.operationalStatus)}>{camera.operationalStatus}</StatusBadge></dd></div>
            <div><dt>Connectivity</dt><dd><StatusBadge tone={connectivityTone} emphasized={connectivityTone === 'danger' && hours >= EMPHASIZE_AFTER_HOURS}>{camera.connectivityStatus}</StatusBadge></dd></div>
            <div><dt>Maintenance</dt><dd><StatusBadge tone={statusTone(camera.maintenanceStatus)}>{camera.maintenanceStatus}</StatusBadge></dd></div>
          </dl>
        </li>;
      })}
    </ul>
  );
}
