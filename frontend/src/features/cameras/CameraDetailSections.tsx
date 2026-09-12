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
  const numberWithUnit = (value: number | null | undefined, unit: string) => value == null ? 'Not recorded' : `${value}${unit}`;
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
          <div><dt>Camera ID</dt><dd>{camera.id}</dd></div>
          <div><dt>Organization unit ID</dt><dd>{camera.organizationUnitId}</dd></div>
          <div><dt>Geographic area ID</dt><dd>{camera.geographicAreaId}</dd></div>
          <div><dt>Type</dt><dd>{camera.cameraType}</dd></div>
          <div><dt>Coordinates</dt><dd>{camera.latitude}, {camera.longitude}</dd></div>
          <div><dt>Manufacturer</dt><dd>{camera.manufacturer ?? 'Not recorded'}</dd></div>
          <div><dt>Model</dt><dd>{camera.model ?? 'Not recorded'}</dd></div>
          <div><dt>Serial number</dt><dd>{camera.serialNumber ?? 'Not recorded'}</dd></div>
          <div><dt>Installation date</dt><dd>{camera.installationDate ?? 'Not recorded'}</dd></div>
          <div><dt>Last seen</dt><dd>{camera.lastSeenAt ? new Date(camera.lastSeenAt).toLocaleString() : 'Not recorded'}</dd></div>
          <div><dt>Last health check</dt><dd>{camera.lastHealthCheckAt ? new Date(camera.lastHealthCheckAt).toLocaleString() : 'Not recorded'}</dd></div>
          <div><dt>Retired at</dt><dd>{camera.retiredAt ? new Date(camera.retiredAt).toLocaleString() : 'Not retired'}</dd></div>
        </dl>
      </section>
      <section aria-labelledby="camera-connection-heading">
        <h3 id="camera-connection-heading">Connection</h3>
        <dl>
          <div><dt>IP address</dt><dd>{camera.ipAddress ?? 'Not recorded'}</dd></div>
          <div><dt>Port</dt><dd>{camera.port ?? 'Not recorded'}</dd></div>
          <div><dt>Protocol</dt><dd>{camera.protocol ?? 'Not recorded'}</dd></div>
          <div><dt>VMS ID</dt><dd>{camera.vmsId ?? 'Not recorded'}</dd></div>
          <div><dt>Stream reference</dt><dd>{camera.streamReference ?? 'Not recorded'}</dd></div>
        </dl>
      </section>
      <section aria-labelledby="camera-coverage-inputs-heading">
        <h3 id="camera-coverage-inputs-heading">Coverage inputs</h3>
        <p>Coverage is an estimate for planning; terrain and obstructions are not modelled.</p>
        <dl>
          <div><dt>Coverage available</dt><dd>{camera.hasCoverage ? 'Yes' : 'No'}</dd></div>
          <div><dt>Altitude</dt><dd>{numberWithUnit(camera.altitude, ' m')}</dd></div>
          <div><dt>Mounting height</dt><dd>{numberWithUnit(camera.mountingHeight, ' m')}</dd></div>
          <div><dt>Azimuth</dt><dd>{numberWithUnit(camera.azimuth, '°')}</dd></div>
          <div><dt>Tilt</dt><dd>{numberWithUnit(camera.tilt, '°')}</dd></div>
          <div><dt>Horizontal field of view</dt><dd>{numberWithUnit(camera.horizontalFov, '°')}</dd></div>
          <div><dt>Vertical field of view</dt><dd>{numberWithUnit(camera.verticalFov, '°')}</dd></div>
          <div><dt>Effective range</dt><dd>{numberWithUnit(camera.effectiveRange, ' m')}</dd></div>
        </dl>
      </section>
    </div>
  );
}
