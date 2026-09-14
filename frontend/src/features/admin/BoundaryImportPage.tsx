import { useMutation, useQuery } from '@tanstack/react-query';
import { useState, type ChangeEvent, type FormEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { BoundaryImportRequest, BoundaryImportResult } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { Button } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import '../cameras/cameras.css';
import { TreeSelect } from '../cameras/TreeSelect';
import { BoundaryMapPicker } from './BoundaryMapPicker';
import './admin.css';

function requestError(error: unknown): string {
  return isApiProblem(error) ? error.detail : 'The boundary import could not be completed. Please try again.';
}

function readTextFile(file: File): Promise<string> {
  if (typeof file.text === 'function') return file.text();
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error('The selected file could not be read.'));
    reader.onload = () => resolve(String(reader.result));
    reader.readAsText(file);
  });
}

/** Parses/validates a pasted or uploaded JSON `BoundaryImportRequest` before it's sent — a
 * transport-shape check only, same posture as the camera bulk importer; the server's own response
 * is the authoritative per-row validation report. */
function parseBoundaryImport(text: string): BoundaryImportRequest {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error('The selected file is not valid JSON.');
  }
  if (typeof parsed !== 'object' || parsed === null || !Array.isArray((parsed as { items?: unknown }).items)) {
    throw new Error('Expected a JSON object with an "items" array.');
  }
  const items = (parsed as { items: unknown[] }).items;
  if (items.length === 0 || items.length > 200) {
    throw new Error('items must contain between 1 and 200 rows.');
  }
  items.forEach((item, index) => {
    if (typeof item !== 'object' || item === null || typeof (item as { geographicAreaId?: unknown }).geographicAreaId !== 'string') {
      throw new Error(`Row ${index + 1}: geographicAreaId is required.`);
    }
    const row = item as { wkt?: unknown; geoJson?: unknown };
    const hasWkt = typeof row.wkt === 'string' && row.wkt.trim().length > 0;
    const hasGeoJson = typeof row.geoJson === 'string' && row.geoJson.trim().length > 0;
    if (hasWkt === hasGeoJson) {
      throw new Error(`Row ${index + 1}: supply exactly one of wkt or geoJson.`);
    }
  });
  return parsed as BoundaryImportRequest;
}

function ImportResultTable({ result }: { result: BoundaryImportResult }) {
  return <section className="boundary-panel" aria-labelledby="boundary-import-result-title">
    <h2 id="boundary-import-result-title">Import results</h2>
    <p role="status">Updated: {result.updated}. Failed: {result.failed}.</p>
    <div className="camera-table-wrap import-preview__table"><table className="camera-table">
      <thead><tr><th>Row</th><th>Geographic area</th><th>Result</th></tr></thead>
      <tbody>{result.rows.map((row) => <tr key={`${row.index}-${row.geographicAreaId}`}>
        <td>{row.index + 1}</td>
        <td>{row.geographicAreaId}</td>
        <td>{row.error ?? row.status}</td>
      </tr>)}</tbody>
    </table></div>
  </section>;
}

/**
 * Admin-only page for `POST /api/v1/geographic-areas/bulk-import-boundaries` — the surveyed
 * boundary polygons `GET /gis/gaps` needs before it can analyze an area (v1.19). No per-area form
 * field on purpose (see that endpoint's own remarks): this is a deliberate GIS-data-loading
 * action, not something to expose inline on every geographic area's edit form.
 *
 * Two ways in: paste one geometry for one area (the common case — a single surveyed boundary
 * just became available), or upload/paste a JSON `BoundaryImportRequest` for many areas at once
 * (the same shape the API itself takes, for a GIS team that already has boundaries staged as
 * files).
 */
export function BoundaryImportPage() {
  useDocumentTitle('Geographic boundaries');

  const geographicAreas = useQuery({
    queryKey: queryKeys.reference.geographicAreas,
    queryFn: ({ signal }) => api.reference.geographicAreas(undefined, signal),
  });

  const importMutation = useMutation({
    mutationFn: (body: BoundaryImportRequest) => api.admin.geography.bulkImportBoundaries(body),
  });

  // --- Single-area quick form ---
  const [areaId, setAreaId] = useState<string | undefined>(undefined);
  const [inputMode, setInputMode] = useState<'draw' | 'paste'>('draw');
  const [geometryFormat, setGeometryFormat] = useState<'geoJson' | 'wkt'>('geoJson');
  const [geometryText, setGeometryText] = useState('');
  const [drawnGeoJson, setDrawnGeoJson] = useState<string | null>(null);
  const [singleError, setSingleError] = useState<string | null>(null);

  function submitSingle(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSingleError(null);
    if (!areaId) {
      setSingleError('Choose a geographic area.');
      return;
    }
    if (inputMode === 'draw') {
      if (!drawnGeoJson) {
        setSingleError('Click the map to draw a boundary — at least 3 points.');
        return;
      }
      importMutation.mutate({ items: [{ geographicAreaId: areaId, geoJson: drawnGeoJson }] });
      return;
    }
    const geometry = geometryText.trim();
    if (!geometry) {
      setSingleError(`Paste the boundary as ${geometryFormat === 'geoJson' ? 'GeoJSON' : 'WKT'}.`);
      return;
    }
    importMutation.mutate({
      items: [{ geographicAreaId: areaId, [geometryFormat]: geometry }],
    });
  }

  // --- Bulk JSON upload ---
  const [bulkRequest, setBulkRequest] = useState<BoundaryImportRequest | null>(null);
  const [bulkFileName, setBulkFileName] = useState<string | null>(null);
  const [bulkError, setBulkError] = useState<string | null>(null);

  async function selectBulkFile(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    setBulkError(null);
    setBulkRequest(null);
    setBulkFileName(null);
    if (!file) return;
    if (!file.name.toLowerCase().endsWith('.json')) {
      setBulkError('Select a .json file shaped as a BoundaryImportRequest.');
      return;
    }
    try {
      setBulkRequest(parseBoundaryImport(await readTextFile(file)));
      setBulkFileName(file.name);
    } catch (reason) {
      setBulkError(reason instanceof Error ? reason.message : 'The JSON import request is invalid.');
    }
  }

  return <section className="onboarding-page" aria-labelledby="boundary-import-title">
    <header>
      <p className="eyebrow">Administration · GIS</p>
      <h1 id="boundary-import-title">Geographic boundaries</h1>
      <p>Attach a surveyed boundary polygon to a geographic area. This is what
        {' '}<code>GET /api/v1/gis/gaps</code> needs before it can run coverage-gap analysis for
        that area — an area with no boundary on file reports "no boundary set," not zero gaps.</p>
    </header>

    <section className="boundary-panel" aria-labelledby="single-boundary-title">
      <h2 id="single-boundary-title">Add one boundary</h2>
      <form className="boundary-form" onSubmit={submitSingle}>
        <div className="boundary-form__geometry">
          {inputMode === 'draw'
            ? <BoundaryMapPicker onChange={setDrawnGeoJson} />
            : <label>{geometryFormat === 'geoJson' ? 'GeoJSON Polygon' : 'WKT POLYGON(...)'}
              <textarea
                rows={14}
                value={geometryText}
                onChange={(event) => setGeometryText(event.target.value)}
                placeholder={geometryFormat === 'geoJson'
                  ? '{"type":"Polygon","coordinates":[[[72.80,23.00],[72.75,23.05],[72.70,23.00],[72.75,22.95],[72.80,23.00]]]}'
                  : 'POLYGON((72.80 23.00, 72.75 23.05, 72.70 23.00, 72.75 22.95, 72.80 23.00))'}
              />
            </label>}
        </div>
        <div className="boundary-form__controls">
          <TreeSelect
            id="boundary-area"
            label="Geographic area"
            items={geographicAreas.data}
            getParentId={(area) => area.parentAreaId}
            value={areaId}
            onChange={setAreaId}
            loading={geographicAreas.isPending}
            error={geographicAreas.isError}
            required
            placeholder="Choose a geographic area"
            emptyMessage="No geographic areas are available."
          />
          <fieldset><legend>How to supply the boundary</legend>
            <label className="checkbox-label"><input checked={inputMode === 'draw'} name="input-mode" onChange={() => setInputMode('draw')} type="radio" /> Draw on map</label>
            <label className="checkbox-label"><input checked={inputMode === 'paste'} name="input-mode" onChange={() => setInputMode('paste')} type="radio" /> Paste GeoJSON/WKT</label>
          </fieldset>
          {inputMode === 'paste' && <fieldset><legend>Geometry format</legend>
            <label className="checkbox-label"><input checked={geometryFormat === 'geoJson'} name="geometry-format" onChange={() => setGeometryFormat('geoJson')} type="radio" /> GeoJSON</label>
            <label className="checkbox-label"><input checked={geometryFormat === 'wkt'} name="geometry-format" onChange={() => setGeometryFormat('wkt')} type="radio" /> WKT</label>
          </fieldset>}
          <p className="field-help">Coordinates are longitude, latitude (WGS84 / EPSG:4326 — no SRID prefix needed) and the ring must close (first point = last point). Replaces any boundary this area already has.</p>
          {singleError && <p className="form-error" role="alert">{singleError}</p>}
          <Button disabled={importMutation.isPending} type="submit">{importMutation.isPending ? 'Importing…' : 'Save boundary'}</Button>
        </div>
      </form>
    </section>

    <section className="boundary-panel" aria-labelledby="bulk-boundary-title">
      <h2 id="bulk-boundary-title">Bulk upload (many areas at once)</h2>
      <p>Upload a JSON file shaped as <code>{'{ "items": [{ "geographicAreaId", "wkt" | "geoJson" }] }'}</code> — 1-200 rows, for a GIS team that already has boundaries staged as files.</p>
      <label className="file-input">Import JSON file<input accept=".json,application/json" aria-describedby={bulkError ? 'bulk-boundary-error' : undefined} onChange={selectBulkFile} type="file" /></label>
      {bulkError && <p className="form-error" id="bulk-boundary-error" role="alert">{bulkError}</p>}
      {bulkRequest && <>
        <p>Preview: {bulkFileName} — {bulkRequest.items.length} row{bulkRequest.items.length === 1 ? '' : 's'} ready for server validation.</p>
        <Button disabled={importMutation.isPending} onClick={() => importMutation.mutate(bulkRequest)} type="button">{importMutation.isPending ? 'Importing…' : 'Import boundaries'}</Button>
      </>}
    </section>

    {importMutation.isError && <p className="form-error" role="alert">{requestError(importMutation.error)}</p>}
    {importMutation.data && <ImportResultTable result={importMutation.data} />}
  </section>;
}
