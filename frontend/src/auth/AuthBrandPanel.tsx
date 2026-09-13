/**
 * The left-hand panel shared by every signed-out screen (sign-in, forced password change).
 * Purely decorative — the SVG is a geometric stand-in for coverage sectors and camera nodes
 * (no image assets exist in the repo) and carries `aria-hidden` since it adds no information.
 */
export function AuthBrandPanel() {
  return (
    <div className="auth-page__brand-panel">
      <svg aria-hidden="true" focusable="false" viewBox="0 0 640 800" preserveAspectRatio="xMidYMid slice">
        <defs>
          <radialGradient id="auth-glow" cx="82%" cy="88%" r="70%">
            <stop offset="0%" stopColor="#38bdf8" stopOpacity=".35" />
            <stop offset="55%" stopColor="#38bdf8" stopOpacity=".08" />
            <stop offset="100%" stopColor="#38bdf8" stopOpacity="0" />
          </radialGradient>
          <linearGradient id="auth-sector-stroke" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stopColor="#7dd3fc" stopOpacity=".5" />
            <stop offset="100%" stopColor="#7dd3fc" stopOpacity="0" />
          </linearGradient>
        </defs>
        <rect x="0" y="0" width="640" height="800" fill="url(#auth-glow)" />
        <g fill="none" stroke="url(#auth-sector-stroke)" strokeWidth="2">
          <path d="M 560 720 L 560 480 A 240 240 0 0 0 402 258 Z" />
          <path d="M 560 720 L 560 380 A 340 340 0 0 0 302 178 Z" />
          <path d="M 560 720 L 560 280 A 440 440 0 0 0 202 98 Z" />
        </g>
        <g fill="white" fillOpacity=".1">
          <circle cx="80" cy="120" r="4" />
          <circle cx="180" cy="90" r="3" />
          <circle cx="120" cy="220" r="3" />
          <circle cx="260" cy="60" r="4" />
          <circle cx="60" cy="320" r="3" />
          <circle cx="220" cy="360" r="4" />
          <circle cx="140" cy="440" r="3" />
          <circle cx="320" cy="140" r="3" />
        </g>
      </svg>
      <div className="auth-page__brand-lockup">
        <svg aria-hidden="true" className="auth-page__logo" focusable="false" viewBox="0 0 48 48">
          <circle cx="24" cy="24" r="23" fill="white" fillOpacity=".1" stroke="white" strokeOpacity=".35" />
          <circle cx="24" cy="24" r="13" fill="none" stroke="white" strokeOpacity=".9" strokeWidth="2" />
          <circle cx="24" cy="24" r="5" fill="white" />
          <path d="M24 3 v6 M24 39 v6 M3 24 h6 M39 24 h6" stroke="white" strokeOpacity=".6" strokeLinecap="round" strokeWidth="2" />
        </svg>
        <p className="auth-page__brand-mark">Trinetra Registry</p>
      </div>
      <div>
        <p className="auth-page__brand-copy">
          Camera registry, VMS federation, and geospatial coverage for state and department operations.
        </p>
        <dl className="auth-page__brand-stats">
          <div><dt>Scale target</dt><dd>80,000 cameras</dd></div>
          <div><dt>Coverage</dt><dd>Multi-department</dd></div>
        </dl>
      </div>
    </div>
  );
}
