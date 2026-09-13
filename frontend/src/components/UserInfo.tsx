import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

import { useAuth } from '../auth/AuthProvider';
import { permissionsFromSession, usernameFromSession } from '../auth/permissions';

/** Avatar + username + permission-count subtitle, with the full permission list and Log out in
 * the menu underneath. There is no "role" claim in this token model (many fine-grained
 * permissions, not one role), so the menu shows the actual permission list rather than an
 * invented role label. */
export function UserInfo() {
  const { session, logout } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);
  const [loggingOut, setLoggingOut] = useState(false);
  const [menuPosition, setMenuPosition] = useState<{ top?: number; bottom?: number; left: number } | null>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  const username = usernameFromSession(session);
  const permissions = permissionsFromSession(session);

  // The trigger lives inside the icon rail, which scrolls its own content (`overflow-y: auto`)
  // — a `position: absolute` menu anchored to the trigger would be clipped by that overflow the
  // instant it extends past the rail's edge. Portaling to <body> with a computed `fixed`
  // position sidesteps the clipping entirely, the same way CameraDetailDrawer does.
  useEffect(() => {
    if (!menuOpen || !triggerRef.current) return undefined;
    const rect = triggerRef.current.getBoundingClientRect();
    const spaceBelow = window.innerHeight - rect.bottom;
    setMenuPosition(
      spaceBelow < 320 && rect.top > 320
        ? { bottom: window.innerHeight - rect.top, left: rect.right + 8 }
        : { top: rect.top, left: rect.right + 8 },
    );

    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      if (!triggerRef.current?.contains(target) && !menuRef.current?.contains(target)) setMenuOpen(false);
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') setMenuOpen(false);
    }
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [menuOpen]);

  async function handleLogout() {
    setLoggingOut(true);
    try {
      await logout();
    } finally {
      // If logout fails to unmount this component (session somehow survives), don't leave the
      // button stuck disabled forever.
      setLoggingOut(false);
    }
  }

  // Only bail out when there's no session at all — an unauthenticated render has nothing to
  // show. If a session exists but the username claim couldn't be decoded, still render the
  // control with a generic fallback: this is the only way to reach the permissions list and
  // Log out, so hiding it entirely because of a decode hiccup would strand the user.
  if (!session) return null;
  const displayName = username ?? 'Account';

  return (
    <div className="user-info">
      <button
        ref={triggerRef}
        aria-expanded={menuOpen}
        aria-haspopup="true"
        className="user-info__trigger"
        type="button"
        onClick={() => setMenuOpen((open) => !open)}
      >
        <span aria-hidden="true" className="user-info__avatar">{displayName.charAt(0).toUpperCase()}</span>
        <span className="user-info__identity">
          <span className="user-info__username">{displayName}</span>
          <span className="user-info__subtitle">{permissions.length} permission{permissions.length === 1 ? '' : 's'}</span>
        </span>
        <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"><path d="m6 9 6 6 6-6" /></svg>
      </button>
      {menuOpen && menuPosition && createPortal(
        <div ref={menuRef} className="user-info__menu" role="menu" style={{ top: menuPosition.top, bottom: menuPosition.bottom, left: menuPosition.left }}>
          <p className="user-info__menu-heading">Permissions</p>
          {permissions.length === 0
            ? <p className="user-info__menu-empty">This account holds no permissions.</p>
            : <ul className="user-info__permission-list">{permissions.map((permission) => <li key={permission}><code>{permission}</code></li>)}</ul>}
          <hr className="user-info__menu-divider" />
          <button className="user-info__menu-item" disabled={loggingOut} role="menuitem" type="button" onClick={() => { setMenuOpen(false); void handleLogout(); }}>
            Log out
          </button>
        </div>,
        document.body,
      )}
    </div>
  );
}
