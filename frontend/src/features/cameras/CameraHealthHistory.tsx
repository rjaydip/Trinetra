import { useQuery } from '@tanstack/react-query';
import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';

export function CameraHealthHistory({ cameraId }: { cameraId: string }) {
  const { session } = useAuth();
  const permitted = hasPermission(session, 'camera.health.read');
  const history = useQuery({ queryKey: ['camera', cameraId, 'health-history'], queryFn: () => api.cameras.healthHistory(cameraId), enabled: permitted });
  const unavailable = !permitted || (isApiProblem(history.error) && history.error.status === 403);

  return <section className="detail-panel" aria-labelledby="camera-health-history-heading">
    <h2 id="camera-health-history-heading">Health history</h2>
    {unavailable ? <p role="status">Health history is unavailable for your permissions.</p>
      : history.isPending ? <p role="status">Loading health history…</p>
        : history.isError ? <div role="alert"><p>Couldn't load health history.</p><button className="button" type="button" onClick={() => history.refetch()}>Try again</button></div>
          : <>
            <p>Returned interval: {new Date(history.data.from).toLocaleString()} – {new Date(history.data.to).toLocaleString()}.</p>
            {history.data.items.length === 0 ? <p>No health checks are available for this interval.</p>
              : <ol className="maintenance-records">{history.data.items.map((check, index) => <li key={`${check.checkedAt}-${index}`}>
                <p><time dateTime={check.checkedAt}>{new Date(check.checkedAt).toLocaleString()}</time></p>
                <dl>
                  <div><dt>Operational</dt><dd>{check.operationalStatus}</dd></div>
                  <div><dt>Connectivity</dt><dd>{check.connectivityStatus}</dd></div>
                  <div><dt>Latency</dt><dd>{check.latencyMs == null ? 'Not recorded' : `${check.latencyMs} ms`}</dd></div>
                  <div><dt>Error code</dt><dd>{check.errorCode ?? 'None recorded'}</dd></div>
                  <div><dt>Failure reason</dt><dd>{check.failureReason ?? 'None recorded'}</dd></div>
                  <div><dt>Source</dt><dd>{check.source}</dd></div>
                </dl>
              </li>)}</ol>}
          </>}
  </section>;
}
