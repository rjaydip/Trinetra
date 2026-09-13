import type { ButtonHTMLAttributes, ReactNode } from 'react';

export function Button({ children, ...props }: ButtonHTMLAttributes<HTMLButtonElement>) {
  return <button className="button" {...props}>{children}</button>;
}

type StatusTone = 'neutral' | 'success' | 'warning' | 'danger';

export function StatusBadge({ children, tone = 'neutral' }: { children: ReactNode; tone?: StatusTone }) {
  return <span className={`status-badge status-badge--${tone}`}>{children}</span>;
}

export function PageState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <section className="page-state" aria-live="polite">
      <h2>{title}</h2>
      {children && <p>{children}</p>}
    </section>
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
