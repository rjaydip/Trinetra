# Trinetra Registry Frontend

This Vite/React application is the Model 1 camera registry and GIS client. It
uses the existing Trinetra Federation API and its seeded records. It does not
create mock cameras or replace an unavailable API with mock data.

## Configuration

Copy `.env.example` to `.env.local` for a local override:

```bash
cp .env.example .env.local
```

`VITE_API_BASE_URL` is the API origin used by authenticated requests. If it is
unset, the frontend uses `http://192.168.1.16:5261`. Do not put credentials or
other secrets in Vite environment files; values prefixed with `VITE_` are
available to the browser.

The API must allow the origin serving this frontend in its explicit
`Auth:AllowedOrigins` configuration. For example, a local Vite server at
`http://localhost:5173` requires that exact origin in the API configuration;
production must list its deployed HTTPS origin. Wildcard origins are not a
substitute for the credentialed allowlist. See `docs/OPERATIONS.md` for API
configuration details.

## Run, test, and build

From this directory:

```bash
npm install
npm run dev -- --host 0.0.0.0
npm test -- --run
npm run typecheck
npm run build
```

The development server normally serves the sign-in screen at
`http://localhost:5173/login`. Vite may proxy API paths during local
development, but the configured API must still be reachable for sign-in and
data.

## Live acceptance

First check the configured backend directly:

```bash
curl --connect-timeout 3 http://192.168.1.16:5261/health
```

When that check succeeds, open the frontend in a browser and use valid
existing credentials to sign in. Confirm that the seeded data supports all of
the following flows:

1. The map displays seeded camera markers and a marker opens its details.
2. The registry contains seeded rows and a row opens the camera detail view.
3. The detail view renders the available camera information and health state.
4. Reports shows the available coverage summary.

If `/health` or a frontend request cannot connect, record the connection error
as an environment/backend availability issue and fix the deployment or network
configuration. The UI surfaces that service as unavailable; it never substitutes
mock registry records, markers, credentials, or coverage calculations.

## Final verification

The complete frontend verification command is:

```bash
npm test -- --run && npm run typecheck && npm run build
```
