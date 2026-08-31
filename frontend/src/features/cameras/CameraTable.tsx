import { Link } from 'react-router-dom';

import type { CameraResponse } from '../../api/models';
import { StatusBadge } from '../../components/ui';

function statusTone(status: string): 'success' | 'warning' | 'danger' | 'neutral' {
  const value = status.toUpperCase();
  if (['ACTIVE', 'ONLINE', 'NORMAL', 'CURRENT'].includes(value)) return 'success';
  if (['OFFLINE', 'FAILED', 'RETIRED'].includes(value)) return 'danger';
  if (['DEGRADED', 'MAINTENANCE', 'UNDER_MAINTENANCE', 'DUE'].includes(value)) return 'warning';
  return 'neutral';
}

export function CameraTable({ cameras }: { cameras: CameraResponse[] }) {
  return (
    <div className="camera-table-wrap">
      <table className="camera-table">
        <caption className="sr-only">Camera registry results</caption>
        <thead><tr><th scope="col">Code</th><th scope="col">Camera</th><th scope="col">Type</th><th scope="col">Operational</th><th scope="col">Connectivity</th><th scope="col">Maintenance</th></tr></thead>
        <tbody>{cameras.map((camera) => (
          <tr key={camera.id}>
            <td><Link to={`/cameras/${camera.id}`}>{camera.cameraCode}</Link></td>
            <td>{camera.name}</td><td>{camera.cameraType}</td>
            <td><StatusBadge tone={statusTone(camera.operationalStatus)}>{camera.operationalStatus}</StatusBadge></td>
            <td><StatusBadge tone={statusTone(camera.connectivityStatus)}>{camera.connectivityStatus}</StatusBadge></td>
            <td><StatusBadge tone={statusTone(camera.maintenanceStatus)}>{camera.maintenanceStatus}</StatusBadge></td>
          </tr>
        ))}</tbody>
      </table>
    </div>
  );
}
