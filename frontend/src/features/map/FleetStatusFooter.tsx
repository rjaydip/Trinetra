import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';

import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { StatusBadge } from '../../components/ui';
import { isWorkerStale } from '../admin/workerHealthStatus';
import './map.css';

/** Two low-priority, "is something quietly broken" signals — a service heartbeat and an alert
 * count, not fleet inventory. Deliberately pills, not sections: each links to its own full page
 * rather than duplicating that page's content here. Each pill is omitted entirely for a caller
 * who lacks the underlying read permission, rather than rendering a 403. */
export function FleetStatusFooter() {
  const { session } = useAuth();
  const canReadWorkers = hasPermission(session, 'worker.read');
  const canReadAlerts = hasPermission(session, 'alert.read');

  const workers = useQuery({
    queryKey: queryKeys.dashboardFooter.workerHealth,
    queryFn: () => api.admin.workerHealth.listPage({ page: 1, pageSize: 50 }),
    enabled: canReadWorkers,
  });
  const alerts = useQuery({
    queryKey: queryKeys.dashboardFooter.unacknowledgedAlerts,
    queryFn: () => api.admin.watchlist.listAlertsPage({ acknowledged: false, page: 1, pageSize: 1 }),
    enabled: canReadAlerts,
  });

  if (!canReadWorkers && !canReadAlerts) return null;

  const staleWorkers = workers.data?.items.filter((worker) => isWorkerStale(worker.lastHeartbeatAt)).length ?? 0;
  const workerTone = workers.isPending ? 'neutral' : staleWorkers > 0 ? 'danger' : 'success';
  const workerLabel = workers.isPending ? 'Checking…' : staleWorkers > 0 ? `${staleWorkers} stale` : 'Healthy';

  const alertCount = alerts.data?.total ?? 0;
  const alertTone = alerts.isPending ? 'neutral' : alertCount > 0 ? 'warning' : 'success';
  const alertLabel = alerts.isPending ? 'Checking…' : alertCount > 0 ? `${alertCount} unacknowledged` : 'None';

  return <div className="dashboard-status-row" aria-label="Additional fleet signals">
    {canReadWorkers && <Link className="dashboard-status-row__item" to="/admin/worker-health">
      <span>AI worker fleet</span>
      <StatusBadge tone={workerTone}>{workerLabel}</StatusBadge>
    </Link>}
    {canReadAlerts && <Link className="dashboard-status-row__item" to="/admin/watchlist">
      <span>Watchlist alerts</span>
      <StatusBadge tone={alertTone}>{alertLabel}</StatusBadge>
    </Link>}
  </div>;
}
