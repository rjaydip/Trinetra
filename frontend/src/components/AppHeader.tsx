import { AppLogo } from './AppLogo';

/** Pure layout slot — the mobile-only menu toggle + brand mark (visible when the sidebar
 * collapses to a drawer; the sidebar carries its own brand mark at wider viewports). User info
 * now lives in the sidebar itself, pinned at the bottom, not here. No search bar: there is no
 * global search feature today, and a decorative input that does nothing would be worse than
 * none. */
export function AppHeader({ onMenuToggle }: { onMenuToggle(): void }) {
  return (
    <header className="app-header">
      <button aria-label="Toggle navigation" className="app-header__menu-toggle" type="button" onClick={onMenuToggle}>
        <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.75"><path d="M4 6h16M4 12h16M4 18h16" /></svg>
      </button>
      <a className="app-header__brand" href="/dashboard">
        <span aria-hidden="true" className="app-header__brand-mark"><AppLogo /></span>
        Trinetra Registry
      </a>
    </header>
  );
}
