import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { useAuth } from '../../auth/AuthProvider';
import { hasPermission } from '../../auth/permissions';
import { PageState } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { DetectionEvidenceModal } from './DetectionEvidenceModal';
import { DetectionTags } from './DetectionTags';
import './detections.css';

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
  useDocumentTitle('Detections');
  const { session } = useAuth();
  const canTag = hasPermission(session, 'observation.write');
  const [plateNumber, setPlateNumber] = useState('');
  const [targetId, setTargetId] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [evidenceFor, setEvidenceFor] = useState<{ eventId: string; occurredAt: string; plateNumber: string | null } | null>(null);

  const targets = useQuery({ queryKey: queryKeys.vms.all, queryFn: ({ signal }) => api.vms.list(signal) });
  const detections = useQuery({
    queryKey: queryKeys.detections(plateNumber, targetId, from, to),
    queryFn: ({ signal }) => api.detections.search({
      plateNumber: plateNumber.trim() || undefined,
      targetId: targetId || undefined,
      from: isoOrUndefined(from),
      to: isoOrUndefined(to),
    }, signal),
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
            <th scope="col">Time</th><th scope="col">Type</th><th scope="col">Plate</th><th scope="col">Vehicle</th><th scope="col">Camera</th><th scope="col">Confidence</th><th scope="col">Evidence</th><th scope="col">Tags</th>
          </tr></thead><tbody>{detections.data.map((detection) => <tr key={detection.id}>
            <td>{new Date(detection.timestamp).toLocaleString()}</td>
            <td>{detection.eventType}</td>
            <td>{detection.plateNumber ?? 'Not reported'}</td>
            <td>{detection.vehicleType ?? 'Not reported'}</td>
            <td>{detection.cameraName ?? detection.cameraId}</td>
            <td>{detection.confidence !== null ? `${Math.round(detection.confidence * 100)}%` : 'Not reported'}</td>
            <td>{detection.snapshotReference
              ? <button className="button button--secondary" type="button" onClick={() => setEvidenceFor({ eventId: detection.id, occurredAt: detection.timestamp, plateNumber: detection.plateNumber })}>View</button>
              : 'None'}</td>
            <td><DetectionTags eventId={detection.id} occurredAt={detection.timestamp} tags={detection.tags} canEdit={canTag} /></td>
          </tr>)}</tbody></table></div>}
    {evidenceFor && <DetectionEvidenceModal
      eventId={evidenceFor.eventId} occurredAt={evidenceFor.occurredAt} plateNumber={evidenceFor.plateNumber}
      onClose={() => setEvidenceFor(null)}
    />}
  </section>;
}
