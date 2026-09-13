import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef, type KeyboardEvent } from 'react';
import { createPortal } from 'react-dom';

import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { CameraDetailSections } from '../cameras/CameraDetailSections';
import './map.css';

export function CameraDetailDrawer({ cameraId, onClose }: { cameraId: string | null; onClose(): void }) {
  const drawer = useRef<HTMLElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const previousFocus = useRef<HTMLElement | null>(null);
  const camera = useQuery({ queryKey: queryKeys.camera.detail(cameraId!), queryFn: () => api.cameras.get(cameraId!), enabled: cameraId !== null });

  useEffect(() => {
    if (!cameraId) return undefined;
    previousFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const background = document.querySelector<HTMLElement>('.app-shell');
    background?.setAttribute('aria-hidden', 'true');
    background?.setAttribute('inert', '');
    closeButton.current?.focus();
    return () => {
      background?.removeAttribute('aria-hidden');
      background?.removeAttribute('inert');
      previousFocus.current?.focus();
    };
  }, [cameraId]);

  if (!cameraId) return null;

  function onKeyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.key === 'Escape') {
      event.preventDefault();
      onClose();
      return;
    }
    if (event.key !== 'Tab') return;

    const focusable = drawer.current?.querySelectorAll<HTMLElement>(
      'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    );
    if (!focusable?.length) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  return createPortal(
    // The onKeyDown below implements the dialog's own focus trap (Tab/Shift+Tab wrapping), the
    // standard accessible-modal pattern; jsx-a11y doesn't treat role="dialog" as "interactive"
    // itself, hence the disable.
    // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions
    <aside
      aria-label="Camera details"
      aria-modal="true"
      className="camera-drawer"
      onKeyDown={onKeyDown}
      ref={drawer}
      role="dialog"
    >
      <div className="camera-drawer__header">
        <h2 tabIndex={-1}>{camera.data?.name ?? 'Camera details'}</h2>
        <button className="button" ref={closeButton} type="button" onClick={onClose}>Close details</button>
      </div>
      {camera.isPending && <p aria-live="polite">Loading camera details…</p>}
      {camera.isError && <p className="form-error" role="alert">Couldn&apos;t load camera details. Close this panel and try again.</p>}
      {camera.data && <CameraDetailSections camera={camera.data} />}
    </aside>
    , document.body,
  );
}
