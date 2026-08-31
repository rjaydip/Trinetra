import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';

import { api } from '../../api/endpoints';
import { CameraDetailSections } from '../cameras/CameraDetailSections';

export function CameraDetailDrawer({ cameraId, onClose }: { cameraId: string | null; onClose(): void }) {
  const closeButton = useRef<HTMLButtonElement>(null);
  const previousFocus = useRef<HTMLElement | null>(null);
  const camera = useQuery({ queryKey: ['camera', cameraId], queryFn: () => api.cameras.get(cameraId!), enabled: cameraId !== null });

  useEffect(() => {
    if (!cameraId) return undefined;
    previousFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    closeButton.current?.focus();
    return () => previousFocus.current?.focus();
  }, [cameraId]);

  if (!cameraId) return null;

  return (
    <aside aria-label="Camera details" aria-modal="true" className="camera-drawer" role="dialog">
      <div className="camera-drawer__header">
        <h2 tabIndex={-1}>{camera.data?.name ?? 'Camera details'}</h2>
        <button className="button" ref={closeButton} type="button" onClick={onClose}>Close details</button>
      </div>
      {camera.isPending && <p aria-live="polite">Loading camera details…</p>}
      {camera.isError && <p className="form-error" role="alert">Couldn&apos;t load camera details. Close this panel and try again.</p>}
      {camera.data && <CameraDetailSections camera={camera.data} />}
    </aside>
  );
}
