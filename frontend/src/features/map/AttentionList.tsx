import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { StatusBadge } from '../../components/ui';
import { downSince, EMPHASIZE_AFTER_HOURS } from '../cameras/downSince';
import './map.css';

const ATTENTION_LIMIT = 10;

/**
 * Top unreachable cameras, oldest-down first — a triage list, not a browse view. VMS targets are
 * deliberately excluded: `VmsResponse` carries no lastSeenAt/downSince equivalent today, so there
 * is nothing to rank them by (see the worker/watchlist footer pills for VMS-adjacent signals).
 */
export function AttentionList() {
  const unreachable = useQuery({
    queryKey: queryKeys.cameras.attentionList,
    queryFn: () => api.cameras.list({ connectivityStatus: 'DISCONNECTED', limit: 200 }),
  });

  return <section aria-labelledby="attention-list-heading">
    <h2 id="attention-list-heading">Needs attention</h2>
    {unreachable.isPending ? <p role="status">Checking for unreachable cameras…</p>
      : unreachable.isError ? <div role="status"><p>{errorDetail(unreachable.error, 'Could not load the attention list.')}</p><button className="button" type="button" onClick={() => unreachable.refetch()}>Retry</button></div>
        : unreachable.data.items.length === 0 ? <p>No unreachable cameras — the fleet is fully connected.</p>
          : <>
            <table className="attention-table">
              <thead>
                <tr><th scope="col">Camera</th><th scope="col">Status</th><th scope="col">Down for</th></tr>
              </thead>
              <tbody>
                {[...unreachable.data.items]
                  .sort((a, b) => downSince(b.lastSeenAt).hours - downSince(a.lastSeenAt).hours)
                  .slice(0, ATTENTION_LIMIT)
                  .map((camera) => {
                    const { label, hours } = downSince(camera.lastSeenAt);
                    return <tr key={camera.id}>
                      <td><Link to={`/cameras/${camera.id}`}>{camera.name}</Link> · {camera.cameraCode}</td>
                      <td><StatusBadge tone="danger" emphasized={hours >= EMPHASIZE_AFTER_HOURS}>Unreachable</StatusBadge></td>
                      <td>{label}</td>
                    </tr>;
                  })}
              </tbody>
            </table>
            {unreachable.data.items.length > ATTENTION_LIMIT && <Link to="/cameras?connectivityStatus=DISCONNECTED">View all {unreachable.data.items.length} unreachable cameras</Link>}
          </>}
  </section>;
}
