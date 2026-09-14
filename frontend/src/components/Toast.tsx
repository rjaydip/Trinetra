import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';

type ToastTone = 'success' | 'error' | 'info';

interface Toast {
  id: number;
  tone: ToastTone;
  message: string;
}

export interface ToastApi {
  success(message: string): void;
  error(message: string): void;
  info(message: string): void;
}

const AUTO_DISMISS_MS = 4000;

const ToastContext = createContext<ToastApi | null>(null);

/** Lets `client.ts` (outside React) raise toasts for ambient events — network failure and
 * session expiry — without a queue rebuilt on the React side. No-op before `ToastProvider`
 * has mounted, e.g. during initial page load. */
export let toastBridge: ToastApi | null = null;

export function useToast(): ToastApi {
  const api = useContext(ToastContext);
  if (!api) throw new Error('useToast must be used within a ToastProvider');
  return api;
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const nextId = useRef(0);

  const dismiss = useCallback((id: number) => {
    setToasts((current) => current.filter((toast) => toast.id !== id));
  }, []);

  const push = useCallback((tone: ToastTone, message: string) => {
    const id = nextId.current++;
    setToasts((current) => [...current, { id, tone, message }]);
    if (tone !== 'error') {
      setTimeout(() => dismiss(id), AUTO_DISMISS_MS);
    }
  }, [dismiss]);

  const api = useMemo<ToastApi>(() => ({
    success: (message) => push('success', message),
    error: (message) => push('error', message),
    info: (message) => push('info', message),
  }), [push]);

  useEffect(() => {
    toastBridge = api;
    return () => {
      if (toastBridge === api) toastBridge = null;
    };
  }, [api]);

  const errorToasts = toasts.filter((toast) => toast.tone === 'error');
  const otherToasts = toasts.filter((toast) => toast.tone !== 'error');

  return (
    <ToastContext.Provider value={api}>
      {children}
      <div className="toast-stack">
        <div className="toast-stack__group" aria-live="polite">
          {otherToasts.map((toast) => <ToastItem key={toast.id} toast={toast} onDismiss={dismiss} />)}
        </div>
        <div className="toast-stack__group">
          {errorToasts.map((toast) => <ToastItem key={toast.id} toast={toast} onDismiss={dismiss} />)}
        </div>
      </div>
    </ToastContext.Provider>
  );
}

function ToastItem({ toast, onDismiss }: { toast: Toast; onDismiss(id: number): void }) {
  return (
    <div className={`toast toast--${toast.tone}`} role={toast.tone === 'error' ? 'alert' : undefined}>
      <p>{toast.message}</p>
      <button aria-label="Dismiss notification" className="toast__close" type="button" onClick={() => onDismiss(toast.id)}>×</button>
    </div>
  );
}
