import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type ChangeEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { BulkImportRequest, BulkImportResult } from '../../api/models';
import { Button } from '../../components/ui';
import { createSampleImport, parseBulkImport } from './import';

function requestError(error: unknown) {
  return isApiProblem(error) ? error.detail : 'Unable to import cameras. Please try again.';
}

function readJsonFile(file: File): Promise<string> {
  if (typeof file.text === 'function') return file.text();
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error('The selected file could not be read.'));
    reader.onload = () => resolve(String(reader.result));
    reader.readAsText(file);
  });
}

export function BulkImportPage() {
  const queryClient = useQueryClient();
  const [request, setRequest] = useState<BulkImportRequest | null>(null);
  const [fileName, setFileName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<BulkImportResult | null>(null);
  const importMutation = useMutation({
    mutationFn: api.cameras.bulkImport,
    onSuccess: async (nextResult) => {
      setResult(nextResult);
      if (nextResult.created + nextResult.updated > 0) {
        await Promise.all([
          queryClient.invalidateQueries({ queryKey: ['cameras'] }),
          queryClient.invalidateQueries({ queryKey: ['camera'] }),
          queryClient.invalidateQueries({ queryKey: ['gis-cameras'] }),
        ]);
      }
    },
  });

  async function selectFile(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    setError(null);
    setResult(null);
    setRequest(null);
    setFileName(null);
    if (!file) return;
    if (!file.name.toLowerCase().endsWith('.json')) {
      setError('Select a .json file exported as a BulkImportRequest.');
      return;
    }
    try {
      setRequest(parseBulkImport(await readJsonFile(file)));
      setFileName(file.name);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'The JSON import request is invalid.');
    }
  }

  function changeMode(mode: BulkImportRequest['mode']) {
    setRequest((current) => current ? { ...current, mode } : current);
    setResult(null);
  }

  function downloadSample() {
    const blob = new Blob([JSON.stringify(createSampleImport(), null, 2)], { type: 'application/json' });
    const objectUrl = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = objectUrl;
    link.download = 'trinetra-camera-import-sample.json';
    link.click();
    URL.revokeObjectURL(objectUrl);
  }

  return <section className="onboarding-page" aria-labelledby="bulk-import-title"><header><p className="eyebrow">Camera registry</p><h1 id="bulk-import-title">Bulk import cameras</h1><p>Upload a JSON <code>BulkImportRequest</code> with 1–500 items. The server validates each row and reports its outcome.</p></header>
    <Button onClick={downloadSample} type="button">Download JSON template</Button>
    <label className="file-input">Import JSON file<input accept=".json,application/json" aria-describedby={error ? 'import-error' : undefined} onChange={selectFile} type="file" /></label>
    {error && <p className="form-error" id="import-error" role="alert">{error}</p>}
    {request && <section className="import-preview" aria-labelledby="import-preview-title"><h2 id="import-preview-title">Preview: {fileName}</h2>
      <fieldset><legend>Import mode</legend><label><input checked={request.mode === 'insert'} name="mode" onChange={() => changeMode('insert')} type="radio" /> Insert new cameras</label><label><input checked={request.mode === 'upsert'} name="mode" onChange={() => changeMode('upsert')} type="radio" /> Create or replace by camera code</label></fieldset>
      <p>{request.items.length} item{request.items.length === 1 ? '' : 's'} ready for server validation.</p>
      <div className="camera-table-wrap import-preview__table"><table className="camera-table"><caption className="sr-only">Imported camera request preview</caption><thead><tr><th>Row</th><th>Camera code</th><th>Name</th><th>Type</th><th>Organization unit</th><th>Geographic area</th></tr></thead><tbody>{request.items.map((item, index) => <tr key={index}><td>{index + 1}</td><td>{item.cameraCode ?? 'Not supplied'}</td><td>{item.name ?? 'Not supplied'}</td><td>{item.cameraType ?? 'Not supplied'}</td><td>{item.organizationUnitId ?? 'Not supplied'}</td><td>{item.geographicAreaId ?? 'Not supplied'}</td></tr>)}</tbody></table></div>
      <Button disabled={importMutation.isPending} onClick={() => importMutation.mutate(request)} type="button">{importMutation.isPending ? 'Importing cameras…' : 'Import cameras'}</Button>
    </section>}
    {importMutation.isError && <p className="form-error" role="alert">{requestError(importMutation.error)}</p>}
    {result && <section className="import-result" aria-labelledby="import-result-title"><h2 id="import-result-title">Import results</h2><p role="status">Created: {result.created}. Updated: {result.updated}. Failed: {result.failed}.</p>
      <div className="camera-table-wrap import-preview__table"><table className="camera-table"><thead><tr><th>Row</th><th>Camera code</th><th>Status</th><th>Result</th></tr></thead><tbody>{result.rows.map((row) => <tr key={`${row.index}-${row.cameraCode}`}><td>{row.index + 1}</td><td>{row.cameraCode}</td><td>{row.status}</td><td>{row.error ?? (row.cameraId ? `Camera ID: ${row.cameraId}` : 'Completed')}</td></tr>)}</tbody></table></div>
    </section>}
  </section>;
}
