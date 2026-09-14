import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ChangeEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { BulkImportRequest, BulkImportResult } from '../../api/models';
import { queryKeys } from '../../api/queryKeys';
import { Button } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { downloadText } from '../../lib/downloadText';
import { camerasToSampleCsv, createSampleCsv, createSampleImport, loadImportReferenceData, parseBulkImport, parseCsvBulkImport, type CsvRowWarning } from './import';
import './cameras.css';

function requestError(error: unknown) {
  return isApiProblem(error) ? error.detail : 'Unable to import cameras. Please try again.';
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

export function BulkImportPage() {
  useDocumentTitle('Bulk import cameras');
  const queryClient = useQueryClient();
  const [request, setRequest] = useState<BulkImportRequest | null>(null);
  const [warnings, setWarnings] = useState<CsvRowWarning[]>([]);
  const [fileName, setFileName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<BulkImportResult | null>(null);
  const [sampleError, setSampleError] = useState<string | null>(null);
  const [sampleLoading, setSampleLoading] = useState(false);

  // CSV columns reference other records (organization unit, geographic area, saved credential) by
  // name rather than by ID — see `import.ts`'s module doc comment. Resolving those names, and
  // writing them back out for the sample downloads, both need this lookup, loaded once up front
  // rather than re-fetched per upload/download.
  const reference = useQuery({
    queryKey: ['bulk-import-reference-data'],
    queryFn: ({ signal }) => loadImportReferenceData(signal),
  });

  const importMutation = useMutation({
    mutationFn: api.cameras.bulkImport,
    onSuccess: async (nextResult) => {
      setResult(nextResult);
      if (nextResult.created + nextResult.updated > 0) {
        await Promise.all([
          queryClient.invalidateQueries({ queryKey: queryKeys.cameras.all }),
          queryClient.invalidateQueries({ queryKey: queryKeys.camera.all }),
          queryClient.invalidateQueries({ queryKey: queryKeys.gisCameras.all }),
        ]);
      }
    },
  });

  async function selectFile(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    setError(null);
    setResult(null);
    setRequest(null);
    setWarnings([]);
    setFileName(null);
    if (!file) return;

    const lowerName = file.name.toLowerCase();
    const isCsv = lowerName.endsWith('.csv');
    const isJson = lowerName.endsWith('.json');
    if (!isCsv && !isJson) {
      setError('Select a .csv or .json file exported as a BulkImportRequest.');
      return;
    }

    if (isCsv && !reference.data) {
      setError(reference.isError
        ? 'Organization unit, geographic area, and credential names could not be loaded — retry the page before uploading a CSV.'
        : 'Still loading organization unit, geographic area, and credential names — try again in a moment.');
      return;
    }

    try {
      const text = await readTextFile(file);
      if (isCsv) {
        const parsed = parseCsvBulkImport(text, reference.data!);
        setRequest(parsed.request);
        setWarnings(parsed.warnings);
      } else {
        setRequest(parseBulkImport(text));
      }
      setFileName(file.name);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : `The ${isCsv ? 'CSV' : 'JSON'} import request is invalid.`);
    }
  }

  function changeMode(mode: BulkImportRequest['mode']) {
    setRequest((current) => current ? { ...current, mode } : current);
    setResult(null);
  }

  function downloadJsonSample() {
    downloadText(JSON.stringify(createSampleImport(), null, 2), 'application/json', 'trinetra-camera-import-sample.json');
  }

  function downloadCsvSample() {
    downloadText(createSampleCsv(), 'text/csv', 'trinetra-camera-import-sample.csv');
  }

  /** Downloads up to 5 *real* cameras already in the registry, in the same CSV shape the import
   * expects — a template built from actually-valid rows (organization unit / geographic area /
   * credential names that exist, etc.) rather than the single made-up example row
   * `downloadCsvSample` offers. Requires a round trip (`camera.read`) unlike the two sample
   * downloads above, so it has its own loading and error state instead of being synchronous. */
  async function downloadExistingCsv() {
    setSampleError(null);
    if (!reference.data) {
      setSampleError(reference.isError
        ? 'Organization unit, geographic area, and credential names could not be loaded — retry the page and try again.'
        : 'Still loading organization unit, geographic area, and credential names — try again in a moment.');
      return;
    }
    setSampleLoading(true);
    try {
      const page = await api.cameras.list({ limit: 5 });
      if (page.items.length === 0) {
        setSampleError('No cameras are registered yet — there is nothing to export as a sample.');
        return;
      }
      downloadText(camerasToSampleCsv(page.items, reference.data), 'text/csv', 'trinetra-camera-import-existing-sample.csv');
    } catch (reason) {
      setSampleError(isApiProblem(reason) ? reason.detail : 'Could not load existing cameras for the sample.');
    } finally {
      setSampleLoading(false);
    }
  }

  return <section className="onboarding-page" aria-labelledby="bulk-import-title"><header><p className="eyebrow">Camera registry</p><h1 id="bulk-import-title">Bulk import cameras</h1><p>Upload a CSV or JSON <code>BulkImportRequest</code> with 1–500 items. In CSV, organization unit, geographic area, and saved credential are entered by name. The server validates each row and reports its outcome.</p></header>
    <div className="import-samples">
      <Button onClick={downloadCsvSample} type="button">Download CSV template</Button>
      <Button onClick={downloadJsonSample} type="button">Download JSON template</Button>
      <Button disabled={sampleLoading} onClick={() => { void downloadExistingCsv(); }} type="button">{sampleLoading ? 'Loading existing cameras…' : 'Download 5 existing cameras as CSV'}</Button>
    </div>
    {sampleError && <p className="form-error" role="alert">{sampleError}</p>}
    <label className="file-input">Import CSV or JSON file<input accept=".csv,.json,text/csv,application/json" aria-describedby={error ? 'import-error' : undefined} onChange={selectFile} type="file" /></label>
    {error && <p className="form-error" id="import-error" role="alert">{error}</p>}
    {warnings.length > 0 && <div className="form-error" role="alert">
      <p>{warnings.length} row{warnings.length === 1 ? '' : 's'} had a name that didn&apos;t match exactly one record — left blank below. Fix the CSV and re-upload, or edit the ID after import.</p>
      <ul>{warnings.map((warning) => <li key={`${warning.row}-${warning.column}`}>{warning.message}</li>)}</ul>
    </div>}
    {request && <section className="import-preview" aria-labelledby="import-preview-title"><h2 id="import-preview-title">Preview: {fileName}</h2>
      <fieldset><legend>Import mode</legend><label><input checked={request.mode === 'insert'} name="mode" onChange={() => changeMode('insert')} type="radio" /> Insert new cameras</label><label><input checked={request.mode === 'upsert'} name="mode" onChange={() => changeMode('upsert')} type="radio" /> Create or replace by camera code</label></fieldset>
      <p>{request.items.length} item{request.items.length === 1 ? '' : 's'} ready for server validation.</p>
      <div className="camera-table-wrap import-preview__table"><table className="camera-table"><caption className="sr-only">Imported camera request preview</caption><thead><tr><th>Row</th><th>Camera code</th><th>Name</th><th>Type</th><th>Organization unit</th><th>Geographic area</th></tr></thead><tbody>{request.items.map((item, index) => <tr key={index}><td>{index + 1}</td><td>{item.cameraCode ?? 'Not supplied'}</td><td>{item.name ?? 'Not supplied'}</td><td>{item.cameraType ?? 'Not supplied'}</td><td>{item.organizationUnitId ? (reference.data?.organizationUnitNameById.get(item.organizationUnitId) ?? item.organizationUnitId) : 'Not supplied'}</td><td>{item.geographicAreaId ? (reference.data?.geographicAreaNameById.get(item.geographicAreaId) ?? item.geographicAreaId) : 'Not supplied'}</td></tr>)}</tbody></table></div>
      <Button disabled={importMutation.isPending} onClick={() => importMutation.mutate(request)} type="button">{importMutation.isPending ? 'Importing cameras…' : 'Import cameras'}</Button>
    </section>}
    {importMutation.isError && <p className="form-error" role="alert">{requestError(importMutation.error)}</p>}
    {result && <section className="import-result" aria-labelledby="import-result-title"><h2 id="import-result-title">Import results</h2><p role="status">Created: {result.created}. Updated: {result.updated}. Failed: {result.failed}.</p>
      <div className="camera-table-wrap import-preview__table"><table className="camera-table"><thead><tr><th>Row</th><th>Camera code</th><th>Status</th><th>Result</th></tr></thead><tbody>{result.rows.map((row) => <tr key={`${row.index}-${row.cameraCode}`}><td>{row.index + 1}</td><td>{row.cameraCode}</td><td>{row.status}</td><td>{row.error ?? (row.cameraId ? `Camera ID: ${row.cameraId}` : 'Completed')}</td></tr>)}</tbody></table></div>
    </section>}
  </section>;
}
