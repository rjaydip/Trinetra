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
  const [organizationId, setOrganizationId] = useState('');
  const [organizationUnitId, setOrganizationUnitId] = useState('');
  const [geographicAreaId, setGeographicAreaId] = useState('');
  const [submittedScope, setSubmittedScope] = useState<{ organizationUnitId?: string; geographicAreaId?: string } | null>(null);
  const [scopeError, setScopeError] = useState<string | null>(null);
  const overview = useQuery({ queryKey: ['overview'], queryFn: api.overview });
  const organizations = useQuery({ queryKey: ['reference', 'organizations'], queryFn: api.reference.organizations });
  const organizationUnits = useQuery({
    queryKey: ['reference', 'organization-units', organizationId],
    queryFn: () => api.reference.organizationUnits(organizationId),
    enabled: Boolean(organizationId),
  });
  const geographicAreas = useQuery({ queryKey: ['reference', 'geographic-areas'], queryFn: () => api.reference.geographicAreas() });
  const coverage = useQuery({
    queryKey: ['coverage-summary', submittedScope],
    queryFn: () => api.gis.coverage(submittedScope!),
    enabled: submittedScope !== null,
  });

  function submitCoverage(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!geographicAreaId) {
      setScopeError('Select a geographic area before loading the coverage summary.');
      return;
    }
    setScopeError(null);
    setSubmittedScope({ organizationUnitId: organizationUnitId || undefined, geographicAreaId: geographicAreaId || undefined });
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
          <label>Coverage geographic area (required)<select aria-describedby={scopeError ? 'coverage-scope-error' : undefined} aria-invalid={Boolean(scopeError)} aria-required="true" value={geographicAreaId} onChange={(event) => setGeographicAreaId(event.target.value)}><option value="">Choose a geographic area</option>{(geographicAreas.data ?? []).map((area) => <option key={area.id} value={area.id}>{area.name} ({area.code})</option>)}</select></label>
          <fieldset><legend>Optional organization-unit narrowing</legend>
            <p>Choose an organization and unit only to narrow the selected geographic area. An organization unit does not define a coverage boundary.</p>
            <label>Organization<select value={organizationId} onChange={(event) => { setOrganizationId(event.target.value); setOrganizationUnitId(''); }}><option value="">Choose an organization</option>{(organizations.data ?? []).map((organization) => <option key={organization.id} value={organization.id}>{organization.name} ({organization.code})</option>)}</select></label>
            <label>Coverage organization unit<select disabled={!organizationId} value={organizationUnitId} onChange={(event) => setOrganizationUnitId(event.target.value)}><option value="">Do not narrow by organization unit</option>{(organizationUnits.data ?? []).map((unit) => <option key={unit.id} value={unit.id}>{unit.name} ({unit.code})</option>)}</select></label>
          </fieldset>
          {scopeError && <p className="form-error" id="coverage-scope-error" role="alert">{scopeError}</p>}
          <button className="button" type="submit">Load coverage summary</button>
        </form>
        {coverage.isPending && submittedScope && <p role="status">Loading coverage summary…</p>}
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
