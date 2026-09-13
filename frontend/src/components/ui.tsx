import { useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode } from 'react';

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
