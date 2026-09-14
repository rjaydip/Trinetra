import { useMutation, useQueries, useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { Link } from 'react-router-dom';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';
import type { CameraResponse } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { StatusBadge, PageState } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { downSince, EMPHASIZE_AFTER_HOURS } from '../cameras/downSince';
import { statusTone } from '../cameras/statusTone';
import { LiveVideoTile } from './LiveVideoTile';
import './videowall.css';

const MIN_COLUMNS = 1;
const MAX_COLUMNS = 12;
const DEFAULT_COLUMN_COUNT = 3;
const MAX_TILES = 64;
const SAVE_DEBOUNCE_MS = 500;

// v3: the wall is no longer a fixed tile count with empty slots — cameraIds is a dynamic list
// (add/remove one at a time) laid out across a configurable columnCount, rows growing to fit.
// Bumping the key avoids misreading an older `{ tileCount, cameraIds: (id|null)[] }` shape.
const STORAGE_KEY = 'trinetra.videowall.v3';

interface WallState {
  columnCount: number;
  cameraIds: string[];
}

function clampColumnCount(value: number): number {
  if (!Number.isFinite(value)) return DEFAULT_COLUMN_COUNT;
  return Math.min(MAX_COLUMNS, Math.max(MIN_COLUMNS, Math.round(value)));
}

/** A best-effort read of a previously-configured wall, for instant first paint only — never
 * treated as "the wall is configured" on its own, since it might be stale, another user's
 * browser profile, or from before the wall was ever saved. */
function loadCachedState(): WallState | null {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<WallState>;
    if (!Array.isArray(parsed.cameraIds) || parsed.cameraIds.length === 0) return null;
    return {
      columnCount: clampColumnCount(typeof parsed.columnCount === 'number' ? parsed.columnCount : DEFAULT_COLUMN_COUNT),
      cameraIds: parsed.cameraIds.filter((id): id is string => typeof id === 'string'),
    };
  } catch {
    return null; // localStorage unavailable (private browsing, quota) or corrupt saved state.
  }
}

function saveCachedState(wall: WallState | null) {
  try {
    if (wall) window.localStorage.setItem(STORAGE_KEY, JSON.stringify(wall));
    else window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    // localStorage unavailable — the local cache is best-effort.
  }
}

/**
 * A configurable multi-camera grid — the RFP's "video wall" surface. Each tile shows a camera's
 * identity/location/health status plus a live HLS feed played through the streaming gateway
 * (`docs/STREAMING-GATEWAY-PLAN.md`; the actual playback lives in `LiveVideoTile`) — never a raw
 * `streamReference` pointed straight at a `<video>` element, which browsers cannot decode (RTSP)
 * and which would bypass this platform's scope/audit checks entirely (see "video is the source,
 * metadata is the product" in `CLAUDE.md`). A tile whose camera has no gateway path provisioned,
 * or where playback genuinely fails, falls back to an honest status-only panel instead of a
 * broken or frozen player.
 *
 * The wall starts **not configured**: a first-time visitor sees an explicit empty state and a
 * "Configure" action, never a pre-filled default grid. Once configured, the same "Configure"
 * action (now in the toolbar corner) re-enters edit mode — column count and which cameras are on
 * the wall, added and removed one at a time, rows growing or shrinking to fit rather than a
 * fixed tile count with empty slots.
 */
export function VideoWallPage() {
  useDocumentTitle('Video wall');

  // `wall` is the one piece of state everything renders from, in both view and configuring mode
  // — there is deliberately no separate "draft vs. server" split. An earlier version derived the
  // view-mode grid from the React Query cache directly and only wrote a `draft` while
  // configuring; clicking "Done" fired the save and flipped modes in the same tick, so the very
  // next render read the *old* (or still-pending) cache value and the wall appeared to vanish —
  // "not configured" again — until/unless the save happened to resolve first. `wall` here is
  // updated immediately and optimistically on every edit and is never reset by a save that is
  // merely in flight or has failed; only a successful `GET`/`PUT`/`DELETE` response changes it.
  const [wall, setWall] = useState<WallState | null>(loadCachedState);
  const [configuredKnown, setConfiguredKnown] = useState(wall !== null);
  const [mode, setMode] = useState<'view' | 'configuring'>('view');
  const [search, setSearch] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [expandedCameraId, setExpandedCameraId] = useState<string | null>(null);
  const expandedTileRef = useRef<HTMLDivElement>(null);

  // The per-user server-saved layout, fetched once to reconcile the local/cached state above.
  // 404 means "never saved" — an explicit, expected state, not an error.
  const preferences = useQuery({
    queryKey: queryKeys.videoWall.preferences,
    queryFn: ({ signal }) => api.videoWall.get(signal),
    retry: false,
  });

  useEffect(() => {
    if (configuredKnown) return; // already reconciled (or the user has started editing) — a later refetch must not clobber in-progress edits.
    if (preferences.isSuccess) {
      setWall({ columnCount: clampColumnCount(preferences.data.columnCount), cameraIds: preferences.data.cameraIds });
      setConfiguredKnown(true);
    } else if (preferences.isError) {
      setConfiguredKnown(true); // confirmed "nothing saved" — stop showing the cached guess as fact.
    }
  }, [configuredKnown, preferences.isSuccess, preferences.isError, preferences.data]);

  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedSearch(search.trim()), 200);
    return () => window.clearTimeout(handle);
  }, [search]);

  useEffect(() => {
    saveCachedState(wall);
  }, [wall]);

  const picker = useQuery({
    queryKey: queryKeys.videoWall.picker(debouncedSearch),
    queryFn: ({ signal }) => api.cameras.list({ q: debouncedSearch || undefined, limit: 50 }, signal),
    enabled: mode === 'configuring',
  });

  const savePreferences = useMutation({
    mutationFn: (body: WallState) => api.videoWall.save({ columnCount: body.columnCount, cameraIds: body.cameraIds }),
  });

  const clearPreferences = useMutation({
    mutationFn: () => api.videoWall.clear(),
    onSuccess: () => {
      setWall(null);
      saveCachedState(null);
      setMode('view');
    },
  });

  // Debounced autosave while configuring: the grid updates instantly (optimistic `wall` state
  // above), but the network write only fires once changes settle for a beat — adding several
  // cameras or resizing columns in a row should not fire a PUT per change. A failed save leaves
  // `wall` exactly as the user left it; it only ever surfaces as the inline note below.
  useEffect(() => {
    if (mode !== 'configuring' || wall === null || wall.cameraIds.length === 0) return undefined;
    const handle = window.setTimeout(() => savePreferences.mutate(wall), SAVE_DEBOUNCE_MS);
    return () => window.clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `savePreferences.mutate` is stable per useMutation instance; including the object would re-fire on every unrelated mutation-state change (isPending/isError).
  }, [mode, wall]);

  // Bounded by MAX_TILES — each tile fetches its own current camera record the same way
  // CameraDetailPage does, so status/location reflects live data rather than going stale.
  const selectedIds = [...new Set(wall?.cameraIds ?? [])];
  const tileCameras = useQueries({
    queries: selectedIds.map((id) => ({
      queryKey: queryKeys.camera.detail(id),
      queryFn: ({ signal }: { signal?: AbortSignal }) => api.cameras.get(id, signal),
    })),
  });
  const cameraById = new Map(selectedIds.map((id, index) => [id, tileCameras[index]]));

  function startConfiguring() {
    setWall((current) => current ?? { columnCount: DEFAULT_COLUMN_COUNT, cameraIds: [] });
    setConfiguredKnown(true); // the user is authoring a wall now — a stale fetch must not overwrite it.
    setMode('configuring');
  }

  function finishConfiguring() {
    if (wall && wall.cameraIds.length > 0) savePreferences.mutate(wall);
    setMode('view');
  }

  function changeColumnCount(raw: string) {
    const parsed = Number(raw);
    if (!Number.isFinite(parsed)) return;
    setWall((current) => current && { ...current, columnCount: clampColumnCount(parsed) });
  }

  function addCamera(cameraId: string) {
    setWall((current) => {
      const base = current ?? { columnCount: DEFAULT_COLUMN_COUNT, cameraIds: [] };
      if (base.cameraIds.includes(cameraId) || base.cameraIds.length >= MAX_TILES) return base;
      return { ...base, cameraIds: [...base.cameraIds, cameraId] };
    });
  }

  function removeCamera(cameraId: string) {
    setWall((current) => current && { ...current, cameraIds: current.cameraIds.filter((id) => id !== cameraId) });
    if (expandedCameraId === cameraId) collapseTile();
  }

  const pickerOptions = (picker.data?.items ?? []).filter((option) => !wall?.cameraIds.includes(option.id));
  const gridStyle = wall ? ({ '--videowall-columns': wall.columnCount } as CSSProperties) : undefined;

  // The overlay is always shown via CSS (position: fixed, full viewport) regardless of whether
  // the browser Fullscreen API is available or the user grants it — that's the part that must
  // always work. `requestFullscreen` on top of it is a best-effort upgrade (hides the browser
  // chrome too) and is silently skipped wherever it's unsupported or denied, e.g. inside an
  // iframe without `allow="fullscreen"`, a locked-down kiosk browser, or in tests (jsdom has no
  // Fullscreen API at all).
  function expandTile(cameraId: string) {
    setExpandedCameraId(cameraId);
  }

  function collapseTile() {
    setExpandedCameraId(null);
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
  }

  useEffect(() => {
    if (expandedCameraId === null) return undefined;

    const node = expandedTileRef.current;
    if (node?.requestFullscreen) void node.requestFullscreen().catch(() => {});

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') collapseTile();
    }
    // The browser's own Escape handling exits fullscreen; this listens for that (and for a
    // manual exit via browser chrome) so the overlay closes in step rather than staying open
    // behind a fullscreen session that already ended.
    function onFullscreenChange() {
      if (!document.fullscreenElement) setExpandedCameraId(null);
    }
    document.addEventListener('keydown', onKeyDown);
    document.addEventListener('fullscreenchange', onFullscreenChange);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
      document.removeEventListener('fullscreenchange', onFullscreenChange);
    };
  }, [expandedCameraId]);

  const expandedTileQuery = expandedCameraId ? cameraById.get(expandedCameraId) : undefined;

  return <section className="videowall-page" aria-labelledby="videowall-title">
    <header className="videowall-header">
      <div>
        <p className="eyebrow">Unified viewing</p>
        <h1 id="videowall-title">Video wall</h1>
        <p>
          A configurable multi-camera grid for situational awareness. Each tile plays a camera&apos;s
          live feed where a viewing session can be established, alongside its current status and
          location — see the note on any tile where live video isn&apos;t available.
        </p>
      </div>
      {mode === 'view' && wall && (
        <button className="button button--secondary videowall-configure" type="button" onClick={startConfiguring}>
          Configure
        </button>
      )}
    </header>

    {mode === 'configuring' && wall && <div className="videowall-toolbar">
      <label className="videowall-columns">
        Columns
        <input
          type="number" min={MIN_COLUMNS} max={MAX_COLUMNS} inputMode="numeric"
          value={wall.columnCount}
          onChange={(event) => changeColumnCount(event.target.value)}
        />
      </label>
      <label className="videowall-search">
        Add camera
        <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search by name or code…" />
      </label>
      {debouncedSearch && <ul className="videowall-picker-results">
        {pickerOptions.length === 0
          ? <li className="videowall-picker-empty">No matching cameras</li>
          : pickerOptions.map((option) => (
            <li key={option.id}>
              <button type="button" onClick={() => addCamera(option.id)}>
                {option.name} ({option.cameraCode})
              </button>
            </li>
          ))}
      </ul>}
      <div className="videowall-toolbar-actions">
        <button className="button" type="button" onClick={finishConfiguring}>Done</button>
        <button
          className="button button--secondary" type="button"
          onClick={() => clearPreferences.mutate()}
          disabled={clearPreferences.isPending}
        >
          Remove wall
        </button>
      </div>
      {savePreferences.isError && <p className="videowall-save-note" role="status">Couldn&apos;t save layout — your changes still work, but won&apos;t be remembered next time.</p>}
    </div>}

    {!wall
      ? !configuredKnown
        ? <PageState title="Loading video wall…">Checking for a saved layout.</PageState>
        : <div className="videowall-empty">
          <PageState title="Video wall is not configured">
            Choose a column count and add cameras to build your wall.
          </PageState>
          <button className="button" type="button" onClick={startConfiguring}>Configure</button>
        </div>
      : <ul className="videowall-grid" style={gridStyle} aria-label="Camera wall tiles">
        {wall.cameraIds.map((cameraId) => {
          const tileQuery = cameraById.get(cameraId);
          return <li className="videowall-tile" key={cameraId}>
            <div className="videowall-tile__actions">
              {mode === 'configuring' && (
                <button
                  type="button" className="videowall-tile__remove"
                  aria-label="Remove this camera from the wall"
                  onClick={() => removeCamera(cameraId)}
                >
                  ×
                </button>
              )}
              {tileQuery?.data && (
                <button
                  type="button" className="videowall-tile__expand"
                  aria-label={`View ${tileQuery.data.name} full screen`}
                  onClick={() => expandTile(cameraId)}
                >
                  ⛶
                </button>
              )}
            </div>
            {tileQuery?.isPending
              ? <p className="videowall-tile__empty">Loading…</p>
              : tileQuery?.isError
                ? <p className="videowall-tile__empty">{errorDetail(tileQuery.error, 'Camera could not be loaded.')}</p>
                : tileQuery?.data
                  ? <VideoWallTileContent camera={tileQuery.data} />
                  : null}
          </li>;
        })}
      </ul>}

    {expandedCameraId && <div className="videowall-fullscreen" ref={expandedTileRef}>
      <button
        type="button" className="videowall-fullscreen__close"
        aria-label="Exit full screen" onClick={collapseTile}
      >
        × Close
      </button>
      {expandedTileQuery?.data
        ? <div className="videowall-fullscreen__content"><VideoWallTileContent camera={expandedTileQuery.data} /></div>
        : <p className="videowall-tile__empty">Loading…</p>}
    </div>}
  </section>;
}

function VideoWallTileContent({ camera }: { camera: CameraResponse }) {
  const connectivityTone = statusTone(camera.connectivityStatus);
  const { hours } = downSince(camera.lastSeenAt);
  return <>
    <div className="videowall-tile__header">
      <p className="eyebrow">{camera.cameraCode}</p>
      <h2><Link to={`/cameras/${camera.id}`}>{camera.name}</Link></h2>
    </div>
    <dl className="videowall-tile__status">
      <div><dt>Operational</dt><dd><StatusBadge tone={statusTone(camera.operationalStatus)}>{camera.operationalStatus}</StatusBadge></dd></div>
      <div><dt>Connectivity</dt><dd><StatusBadge tone={connectivityTone} emphasized={connectivityTone === 'danger' && hours >= EMPHASIZE_AFTER_HOURS}>{camera.connectivityStatus}</StatusBadge></dd></div>
      <div><dt>Last seen</dt><dd>{camera.lastSeenAt ? new Date(camera.lastSeenAt).toLocaleString() : 'Never reported'}</dd></div>
    </dl>
    <div className="videowall-tile__feed">
      <LiveVideoTile cameraId={camera.id} />
      {camera.streamReference && <p className="videowall-tile__reference">Stream reference: <code>{camera.streamReference}</code></p>}
    </div>
  </>;
}
