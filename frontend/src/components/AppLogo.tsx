/** The app's mark — same lens/aperture motif as `public/favicon.svg`, kept in sync by hand since
 * the favicon must be a static file the browser can request directly. */
export function AppLogo() {
  return (
    <svg aria-hidden="true" viewBox="0 0 48 48">
      <rect width="48" height="48" rx="10" fill="currentColor" />
      <circle cx="24" cy="24" r="14" fill="none" stroke="white" strokeWidth="2.5" />
      <circle cx="24" cy="24" r="5" fill="white" />
      <path d="M24 4v6M24 38v6M4 24h6M38 24h6" stroke="white" strokeOpacity=".7" strokeWidth="2.5" strokeLinecap="round" />
    </svg>
  );
}
