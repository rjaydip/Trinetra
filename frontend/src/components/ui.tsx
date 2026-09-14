import { useEffect, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

export function Button({ children, ...props }: ButtonHTMLAttributes<HTMLButtonElement>) {
  return <button className="button" {...props}>{children}</button>;
}

type StatusTone = 'neutral' | 'success' | 'warning' | 'danger';

/** `emphasized` layers a stronger visual weight on top of `tone` — e.g. a camera down for days
 * versus one down for minutes both read as `danger`, but only the former should demand attention
 * first. It never changes the color itself, only intensity, so "red still means bad" stays true
 * app-wide. */
export function StatusBadge({ children, tone = 'neutral', emphasized = false }: { children: ReactNode; tone?: StatusTone; emphasized?: boolean }) {
  return <span className={`status-badge status-badge--${tone}${emphasized ? ' status-badge--emphasized' : ''}`}>{children}</span>;
}

export function PageState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <section className="page-state" aria-live="polite">
      <h2>{title}</h2>
      {children && <p>{children}</p>}
    </section>
  );
}

/** A password `<input>` with a show/hide toggle. Visibility state is local — never lifted. */
export function PasswordInput({ id, ...props }: InputHTMLAttributes<HTMLInputElement> & { id: string }) {
  const [visible, setVisible] = useState(false);
  return (
    <div className="password-field">
      <input {...props} id={id} type={visible ? 'text' : 'password'} />
      <button
        aria-label={visible ? 'Hide password' : 'Show password'}
        aria-pressed={visible}
        className="password-field__toggle"
        type="button"
        onClick={() => setVisible((current) => !current)}
      >
        {visible ? 'Hide' : 'Show'}
      </button>
    </div>
  );
}

/** A small centered overlay dialog. Closes on Escape or an overlay click; `onClose` is the only
 * way out otherwise, since a form inside decides for itself when it's done (submit, or its own
 * Cancel button) rather than the modal guessing.
 *
 * Rendered through a portal into `document.body`, not inline where it's invoked — a caller that
 * opens this from inside its own `<form>` (e.g. the "Add credential" picker inside the camera
 * form) would otherwise put this modal's `<form>` in the DOM as a *nested* form, which HTML
 * doesn't allow. A nested form's submit button can end up triggering the outer form's native
 * submission instead of the inner one's React `onSubmit` — a full, un-prevented page reload
 * instead of the save actually running. Portalling to `document.body` keeps the two forms as
 * unrelated siblings in the DOM regardless of where the modal is opened from. */
export function Modal({ titleId, children, onClose }: { titleId: string; children: ReactNode; onClose(): void }) {
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [onClose]);

  return createPortal(
    <div className="modal-overlay" onClick={onClose}>
      <div aria-labelledby={titleId} aria-modal="true" className="modal" role="dialog" onClick={(event) => event.stopPropagation()}>
        {children}
      </div>
    </div>,
    document.body,
  );
}

export function Pager({ page, pageSize, total, onPageChange }: {
  page: number;
  pageSize: number;
  total: number;
  onPageChange(page: number): void;
}) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  if (totalPages <= 1) return null;
  return (
    <nav className="pager" aria-label="Pagination">
      <button className="button button--secondary" type="button" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>Previous</button>
      <span className="pager__status">Page {page} of {totalPages} ({total} total)</span>
      <button className="button button--secondary" type="button" disabled={page >= totalPages} onClick={() => onPageChange(page + 1)}>Next</button>
    </nav>
  );
}
