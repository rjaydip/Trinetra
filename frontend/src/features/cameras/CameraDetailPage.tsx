import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { MaintenanceRecordResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { PageState, StatusBadge } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { CameraDetailSections } from './CameraDetailSections';
import { CameraHealthHistory } from './CameraHealthHistory';
import { statusTone } from './statusTone';
import './cameras.css';

function dateTime(value: string | null) {
  return value ? new Date(value).toLocaleString() : 'Not recorded';
}

function unavailable(error: unknown, subject: string) {
  if (isApiProblem(error) && error.status === 403) return `${subject} is unavailable for your permissions.`;
  return `Couldn't load ${subject.toLowerCase()}.`;
}

function MaintenanceList({ records }: { records: MaintenanceRecordResponse[] }) {
  if (records.length === 0) return <p>No maintenance records are available.</p>;
  return <ul className="maintenance-records">{records.map((record) => <li key={record.id}>
    <div><strong>{record.maintenanceType}</strong> <StatusBadge>{record.status}</StatusBadge></div>
    <p>{record.description}</p>
    {record.failureReason && <p>Failure reason: {record.failureReason}</p>}
    <p>Reported: {dateTime(record.reportedAt)}{record.nextDueAt ? ` · Next due: ${dateTime(record.nextDueAt)}` : ''}</p>
  </li>)}</ul>;
}

export function CameraDetailPage() {
  const { cameraId } = useParams();
  const { session } = useAuth();
  const camera = useQuery({ queryKey: queryKeys.camera.detail(cameraId!), queryFn: ({ signal }) => api.cameras.get(cameraId!, signal), enabled: Boolean(cameraId) });
  const health = useQuery({ queryKey: queryKeys.camera.health(cameraId!), queryFn: ({ signal }) => api.cameras.health(cameraId!, signal), enabled: Boolean(camera.data) });
  const maintenance = useQuery({ queryKey: queryKeys.camera.maintenance(cameraId!), queryFn: ({ signal }) => api.cameras.maintenance(cameraId!, undefined, signal), enabled: Boolean(camera.data) });
  useDocumentTitle(camera.data?.cameraCode ?? 'Camera detail');

  if (camera.isPending) return <PageState title="Loading camera details">Retrieving the current registry record…</PageState>;
  if (camera.isError || !camera.data) return <PageState title="Couldn&apos;t load camera details">Return to the registry and select a camera again.</PageState>;

  return (
    <section className="camera-detail-page" aria-labelledby="camera-detail-title">
      <Link className="back-link" to="/cameras">Back to camera registry</Link>
      <header className="camera-detail-page__header">
        <div><p className="eyebrow">{camera.data.cameraCode}</p><h1 id="camera-detail-title">{camera.data.name}</h1></div>
        {hasPermission(session, 'camera.update') && <Link className="button button--secondary" to={`/cameras/${camera.data.id}/edit`}>Edit camera</Link>}
      </header>
      <CameraDetailSections camera={camera.data} />
      <section aria-labelledby="camera-health-heading" className="detail-panel"><h2 id="camera-health-heading">Health</h2>
        {health.isPending && <p aria-live="polite">Loading health information…</p>}
        {health.isError && <p role="status">{unavailable(health.error, 'Health information')}</p>}
        {health.data && <dl><div><dt>Operational</dt><dd><StatusBadge tone={statusTone(health.data.operationalStatus)}>{health.data.operationalStatus}</StatusBadge></dd></div><div><dt>Connectivity</dt><dd><StatusBadge tone={statusTone(health.data.connectivityStatus)}>{health.data.connectivityStatus}</StatusBadge></dd></div><div><dt>Last seen</dt><dd>{dateTime(health.data.lastSeenAt)}</dd></div><div><dt>Last checked</dt><dd>{dateTime(health.data.lastHealthCheckAt)}</dd></div>{health.data.failureReason && <div><dt>Failure reason</dt><dd>{health.data.failureReason}</dd></div>}</dl>}
      </section>
      <CameraHealthHistory cameraId={camera.data.id} />
      <section aria-labelledby="camera-maintenance-heading" className="detail-panel"><h2 id="camera-maintenance-heading">Maintenance</h2>
        {maintenance.isPending && <p aria-live="polite">Loading maintenance records…</p>}
        {maintenance.isError && <p role="status">{unavailable(maintenance.error, 'Maintenance information')}</p>}
        {maintenance.data && <MaintenanceList records={maintenance.data} />}
      </section>
    </section>
  );
}
