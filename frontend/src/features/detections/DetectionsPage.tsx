import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { PageState } from '../../components/ui';

function errorDetail(error: unknown, fallback: string) {
  return isApiProblem(error) ? error.detail : fallback;
}

function isoOrUndefined(value: string): string | undefined {
  if (!value) return undefined;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
}

/**
 * `GET /api/v1/detections` — the "search metadata" step of the demo path. Read-only: ingest is
 * the standalone AI worker's own job (`POST /detections`), never a UI action.
 */
export function DetectionsPage() {
  const [plateNumber, setPlateNumber] = useState('');
  const [targetId, setTargetId] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');

  const targets = useQuery({ queryKey: ['vms'], queryFn: api.vms.list });
  const detections = useQuery({
    queryKey: ['detections', plateNumber, targetId, from, to],
    queryFn: () => api.detections.search({
      plateNumber: plateNumber.trim() || undefined,
      targetId: targetId || undefined,
      from: isoOrUndefined(from),
      to: isoOrUndefined(to),
    }),
  });

  return <section className="detections-page" aria-labelledby="detections-title">
    <header>
      <p className="eyebrow">Video analytics</p>
      <h1 id="detections-title">Detections</h1>
      <p>Vehicle, plate and OCR detections submitted by the AI worker — the last 24 hours by default.</p>
    </header>
    <fieldset className="registry-filters">
      <legend>Search filters</legend>
      <label>Plate number<input value={plateNumber} onChange={(event) => setPlateNumber(event.target.value)} /></label>
      <label>VMS target
        <select value={targetId} onChange={(event) => setTargetId(event.target.value)}>
          <option value="">All targets</option>
          {(targets.data ?? []).map((target) => <option key={target.id} value={target.id}>{target.displayName}</option>)}
        </select>
      </label>
      <label>From<input type="datetime-local" value={from} onChange={(event) => setFrom(event.target.value)} /></label>
      <label>To<input type="datetime-local" value={to} onChange={(event) => setTo(event.target.value)} /></label>
    </fieldset>
    {detections.isPending ? <PageState title="Loading detections">Retrieving matching detections…</PageState>
      : detections.isError ? <><PageState title="Couldn&apos;t load detections">{errorDetail(detections.error, 'Detections could not be loaded.')}</PageState><button className="button" type="button" onClick={() => detections.refetch()}>Try again</button></>
        : detections.data.length === 0 ? <PageState title="No detections">No detections matched this search.</PageState>
          : <div className="camera-table-wrap"><table className="camera-table"><caption>{detections.data.length} detection{detections.data.length === 1 ? '' : 's'}</caption><thead><tr>
            <th scope="col">Time</th><th scope="col">Type</th><th scope="col">Plate</th><th scope="col">Vehicle</th><th scope="col">Camera</th><th scope="col">Confidence</th>
          </tr></thead><tbody>{detections.data.map((detection) => <tr key={detection.id}>
            <td>{new Date(detection.timestamp).toLocaleString()}</td>
            <td>{detection.eventType}</td>
            <td>{detection.plateNumber ?? 'Not reported'}</td>
            <td>{detection.vehicleType ?? 'Not reported'}</td>
            <td>{detection.cameraId}</td>
            <td>{detection.confidence !== null ? `${Math.round(detection.confidence * 100)}%` : 'Not reported'}</td>
          </tr>)}</tbody></table></div>}
  </section>;
}
