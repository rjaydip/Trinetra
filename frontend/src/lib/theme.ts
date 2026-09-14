export type Theme = 'light' | 'dark';

const STORAGE_KEY = 'trinetra.theme';

/** The persisted value, or null when the user has never made an explicit choice — in that
 * case `data-theme` stays unset and `prefers-color-scheme` alone governs the palette (see the
 * inline script in index.html, which applies this same rule before first paint). */
export function getStoredTheme(): Theme | null {
  const value = localStorage.getItem(STORAGE_KEY);
  return value === 'light' || value === 'dark' ? value : null;
}

export function setStoredTheme(theme: Theme | null) {
  if (theme) {
    localStorage.setItem(STORAGE_KEY, theme);
    document.documentElement.setAttribute('data-theme', theme);
  } else {
    localStorage.removeItem(STORAGE_KEY);
    document.documentElement.removeAttribute('data-theme');
  }
}
