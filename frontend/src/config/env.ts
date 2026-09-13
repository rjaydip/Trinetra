// Every `import.meta.env` read in the app goes through this one file. Two separate reads of the
// same variable (one in the API client, one in a login form) is how a value silently drifts —
// one call site trims a trailing slash and the other doesn't, one has a fallback and the other
// throws. Centralizing them means there is exactly one place to look, and exactly one place to
// change when a variable is renamed.

const DEFAULT_API_BASE_URL = 'http://localhost:5261';

/** The backend origin every API request is sent to. Trailing slash stripped once, here. */
export function apiBaseUrl(): string {
  return (import.meta.env.VITE_API_BASE_URL || DEFAULT_API_BASE_URL).replace(/\/$/, '');
}

/**
 * Local-dev convenience only: pre-fills the login form from `.env.local` so a developer isn't
 * retyping credentials on every reload. Never set in a shared `.env` — these values are baked
 * into the client bundle at build time like any other `VITE_*` variable.
 */
export function devCredentials(): { username: string; password: string } {
  // Gated on DEV so a stray VITE_USERNAME/VITE_PASSWORD left in a shared or production .env file
  // is ignored by the production build rather than baked into the client bundle.
  if (!import.meta.env.DEV) return { username: '', password: '' };
  return {
    username: import.meta.env.VITE_USERNAME ?? '',
    password: import.meta.env.VITE_PASSWORD ?? '',
  };
}
