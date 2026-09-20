import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import { Modal, PageState } from '../../components/ui';

/** Fetched as a blob (`api.detections.evidence`), never a plain `<img src="...">` — the route
 * requires an `Authorization` header an `<img>` tag can't send. The object URL is per-mount and
 * revoked on unmount/refetch so a viewer never accumulates one per detection opened. */
export function DetectionEvidenceModal({ eventId, occurredAt, plateNumber, onClose }: {
  eventId: string;
  occurredAt: string;
  plateNumber: string | null;
  onClose(): void;
}) {
  const titleId = `evidence-modal-${eventId}`;
  const evidence = useQuery({
    queryKey: ['detection-evidence', eventId, occurredAt],
    queryFn: ({ signal }) => api.detections.evidence(eventId, occurredAt, signal),
  });
  const [objectUrl, setObjectUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!evidence.data) return;
    const url = URL.createObjectURL(evidence.data);
    setObjectUrl(url);
    return () => URL.revokeObjectURL(url);
  }, [evidence.data]);

  return <Modal titleId={titleId} onClose={onClose}>
    <h2 id={titleId}>{plateNumber ?? 'Detection'} evidence</h2>
    {evidence.isPending ? <PageState title="Loading evidence">Retrieving the snapshot…</PageState>
      : evidence.isError ? <PageState title="Couldn&apos;t load evidence">{errorDetail(evidence.error, 'The evidence image could not be loaded.')}</PageState>
        : objectUrl && <img className="detection-evidence__image" src={objectUrl} alt={plateNumber ? `Evidence snapshot for plate ${plateNumber}` : 'Detection evidence snapshot'} />}
    <div className="form-actions">
      <button className="button button--secondary" type="button" onClick={onClose}>Close</button>
    </div>
  </Modal>;
}
