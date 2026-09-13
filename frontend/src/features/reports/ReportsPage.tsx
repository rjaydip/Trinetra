import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { StatusBadge } from '../../components/ui';
import { TreeSelect } from '../cameras/TreeSelect';
import { CoverageSummary } from './CoverageSummary';
import './reports.css';

function reportError(error: unknown): string {
  return isApiProblem(error) ? error.detail : 'The report could not be loaded. Please try again.';
}

export function ReportsPage() {
  const [organizationId, setOrganizationId] = useState('');
  const [organizationUnitId, setOrganizationUnitId] = useState('');
  const [geographicAreaId, setGeographicAreaId] = useState('');
  const [submittedScope, setSubmittedScope] = useState<{ organizationUnitId?: string; geographicAreaId?: string } | null>(null);
  const [scopeError, setScopeError] = useState<string | null>(null);
  const overview = useQuery({ queryKey: queryKeys.overview, queryFn: api.overview });
  const organizations = useQuery({ queryKey: queryKeys.reference.organizations, queryFn: api.reference.organizations });
  const organizationUnits = useQuery({
    queryKey: queryKeys.reference.organizationUnits(organizationId),
    queryFn: () => api.reference.organizationUnits(organizationId),
    enabled: Boolean(organizationId),
  });
  const geographicAreas = useQuery({ queryKey: queryKeys.reference.geographicAreas, queryFn: () => api.reference.geographicAreas() });
  const coverage = useQuery({
    queryKey: queryKeys.coverageSummary(submittedScope),
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

  return (
    <section className="reports-page" aria-labelledby="reports-title">
      <header>
        <p className="eyebrow">Registry reporting</p>
        <h1 id="reports-title">Reports</h1>
        <p>Fleet totals are live. Coverage summaries are based on the selected area and remain estimated planning aids.</p>
      </header>

      <section aria-labelledby="fleet-summary-title">
        <h2 id="fleet-summary-title">Fleet summary</h2>
        {overview.isPending ? <p role="status">Retrieving current fleet totals…</p>
          : overview.isError || !overview.data ? <div role="status">
            <p>{reportError(overview.error)}</p>
            <button className="button" type="button" onClick={() => overview.refetch()}>Retry fleet summary</button>
          </div>
            : <dl className="report-metrics">
              <div><dt>Total cameras</dt><dd>{overview.data.cameras}</dd></div>
              <div><dt>Unreachable cameras</dt><dd><StatusBadge tone="warning">{overview.data.unreachableCameras} unreachable</StatusBadge></dd><p>Warning: unreachable cameras need attention.</p></div>
              <div><dt>VMS targets connected</dt><dd>{overview.data.activeTargets} / {overview.data.targets}</dd></div>
              <div><dt>Quarantined targets</dt><dd>{overview.data.quarantinedTargets > 0 ? <StatusBadge tone="warning">{overview.data.quarantinedTargets}</StatusBadge> : overview.data.quarantinedTargets}</dd></div>
            </dl>}
      </section>

      <section className="report-panel" aria-labelledby="coverage-summary-title">
        <div className="report-panel__header"><div><h2 id="coverage-summary-title">Coverage summary</h2><p>Estimated planning aid only; terrain and obstructions are not modelled.</p></div><StatusBadge tone="warning">Estimated</StatusBadge></div>
        <form className="coverage-scope-form" onSubmit={submitCoverage}>
          <TreeSelect
            id="reports-geographic-area"
            label="Coverage geographic area (required)"
            items={geographicAreas.data}
            getParentId={(area) => area.parentAreaId}
            value={geographicAreaId || undefined}
            onChange={(id) => setGeographicAreaId(id ?? '')}
            loading={geographicAreas.isPending}
            error={geographicAreas.isError}
            required
            invalid={Boolean(scopeError)}
            describedBy={scopeError ? 'coverage-scope-error' : undefined}
            placeholder="Choose a geographic area"
            emptyMessage="No geographic areas are available."
          />
          <fieldset><legend>Optional organization-unit narrowing</legend>
            <p>Choose an organization and unit only to narrow the selected geographic area. An organization unit does not define a coverage boundary.</p>
            <label>Organization<select value={organizationId} onChange={(event) => { setOrganizationId(event.target.value); setOrganizationUnitId(''); }}><option value="">Choose an organization</option>{(organizations.data ?? []).map((organization) => <option key={organization.id} value={organization.id}>{organization.name} ({organization.code})</option>)}</select></label>
            <TreeSelect
              id="reports-organization-unit"
              label="Coverage organization unit"
              items={organizationUnits.data}
              getParentId={(unit) => unit.parentUnitId}
              value={organizationUnitId || undefined}
              onChange={(id) => setOrganizationUnitId(id ?? '')}
              disabled={!organizationId}
              loading={organizationUnits.isPending}
              error={organizationUnits.isError}
              placeholder="Do not narrow by organization unit"
              emptyMessage="This organization has no units."
            />
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
