import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { PageState, StatusBadge } from '../../components/ui';
import { CoverageSummary } from './CoverageSummary';

function reportError(error: unknown): string {
  return isApiProblem(error) ? error.detail : 'The report could not be loaded. Please try again.';
}

export function ReportsPage() {
  const [boundingBox, setBoundingBox] = useState('');
  const [submittedBoundingBox, setSubmittedBoundingBox] = useState<string | null>(null);
  const [scopeError, setScopeError] = useState<string | null>(null);
  const overview = useQuery({ queryKey: ['overview'], queryFn: api.overview });
  const coverage = useQuery({
    queryKey: ['coverage-summary', submittedBoundingBox],
    queryFn: () => api.gis.coverage({ bbox: submittedBoundingBox! }),
    enabled: submittedBoundingBox !== null,
  });

  function submitCoverage(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const value = boundingBox.trim();
    if (!value) {
      setScopeError('Enter a bounding box before loading the coverage summary.');
      return;
    }
    setScopeError(null);
    setSubmittedBoundingBox(value);
  }

  if (overview.isPending) return <PageState title="Loading fleet report">Retrieving current fleet totals…</PageState>;
  if (overview.isError || !overview.data) return <PageState title="Couldn&apos;t load fleet report">{reportError(overview.error)}</PageState>;

  return (
    <section className="reports-page" aria-labelledby="reports-title">
      <header>
        <p className="eyebrow">Registry reporting</p>
        <h1 id="reports-title">Reports</h1>
        <p>Fleet totals are live. Coverage summaries are based on the selected area and remain estimated planning aids.</p>
      </header>

      <section aria-labelledby="fleet-summary-title">
        <h2 id="fleet-summary-title">Fleet summary</h2>
        <dl className="report-metrics">
          <div><dt>Total cameras</dt><dd>{overview.data.cameras}</dd></div>
          <div><dt>Unreachable cameras</dt><dd><StatusBadge tone="warning">{overview.data.unreachableCameras} unreachable</StatusBadge></dd><p>Warning: unreachable cameras need attention.</p></div>
        </dl>
      </section>

      <section className="report-panel" aria-labelledby="coverage-summary-title">
        <div className="report-panel__header"><div><h2 id="coverage-summary-title">Coverage summary</h2><p>Estimated planning aid only; terrain and obstructions are not modelled.</p></div><StatusBadge tone="warning">Estimated</StatusBadge></div>
        <form className="coverage-scope-form" onSubmit={submitCoverage}>
          <label htmlFor="coverage-bounding-box">Coverage bounding box <input id="coverage-bounding-box" value={boundingBox} onChange={(event) => setBoundingBox(event.target.value)} placeholder="west,south,east,north" /></label>
          <p id="coverage-scope-help">Use coordinates in west,south,east,north order for the area you are assessing.</p>
          {scopeError && <p className="form-error" role="alert">{scopeError}</p>}
          <button className="button" type="submit">Load coverage summary</button>
        </form>
        {coverage.isPending && submittedBoundingBox && <p role="status">Loading coverage summary…</p>}
        {coverage.isError && <p className="form-error" role="alert">{reportError(coverage.error)}</p>}
        {coverage.data && <CoverageSummary coverage={coverage.data} />}
      </section>

      <section className="report-unavailable" aria-labelledby="coverage-gap-title">
        <StatusBadge tone="neutral">Unavailable</StatusBadge>
        <h2 id="coverage-gap-title">Coverage-gap analysis is not available yet</h2>
        <p>The registry API does not expose coverage-gap results, so this report does not estimate or show a gap count.</p>
      </section>
      <section className="report-unavailable" aria-labelledby="ageing-title">
        <StatusBadge tone="neutral">Unavailable</StatusBadge>
        <h2 id="ageing-title">Ageing infrastructure reporting is not available yet</h2>
        <p>The registry API does not expose infrastructure-age data, so no ageing metric is shown.</p>
      </section>
    </section>
  );
}
