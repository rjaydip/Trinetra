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
