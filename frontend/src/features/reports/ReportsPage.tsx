import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { queryKeys } from '../../api/queryKeys';
import { StatusBadge } from '../../components/ui';
import { useDocumentTitle } from '../../lib/useDocumentTitle';
import { TreeSelect } from '../cameras/TreeSelect';
import { CoverageSummary } from './CoverageSummary';
import './reports.css';

function reportError(error: unknown): string {
  return isApiProblem(error) ? error.detail : 'The report could not be loaded. Please try again.';
}

export function ReportsPage() {
  useDocumentTitle('Reports');
  const [organizationId, setOrganizationId] = useState('');
  const [organizationUnitId, setOrganizationUnitId] = useState('');
  const [geographicAreaId, setGeographicAreaId] = useState('');
  const [submittedScope, setSubmittedScope] = useState<{ organizationUnitId?: string; geographicAreaId?: string } | null>(null);
  const [scopeError, setScopeError] = useState<string | null>(null);
  const overview = useQuery({ queryKey: queryKeys.overview, queryFn: ({ signal }) => api.overview(signal) });
  const ageing = useQuery({ queryKey: queryKeys.ageingInfrastructure, queryFn: ({ signal }) => api.cameras.ageingInfrastructure({}, signal) });
  const organizations = useQuery({ queryKey: queryKeys.reference.organizations, queryFn: ({ signal }) => api.reference.organizations(signal) });
  const organizationUnits = useQuery({
    queryKey: queryKeys.reference.organizationUnits(organizationId),
    queryFn: ({ signal }) => api.reference.organizationUnits(organizationId, signal),
    enabled: Boolean(organizationId),
  });
  const geographicAreas = useQuery({ queryKey: queryKeys.reference.geographicAreas, queryFn: ({ signal }) => api.reference.geographicAreas(undefined, signal) });
  const coverage = useQuery({
    queryKey: queryKeys.coverageSummary(submittedScope),
    queryFn: ({ signal }) => api.gis.coverage(submittedScope!, signal),
    enabled: submittedScope !== null,
  });
  // Gap analysis needs a surveyed boundary on the area itself (PostGIS, v1.19) — narrowing by
  // organization unit doesn't apply to it the way it does the coverage-summary buckets above, so
  // this reuses only the geographic area half of the same submitted scope.
  const gaps = useQuery({
    queryKey: queryKeys.coverageGaps(submittedScope?.geographicAreaId ?? null),
    queryFn: ({ signal }) => api.gis.gaps(submittedScope!.geographicAreaId!, signal),
    enabled: Boolean(submittedScope?.geographicAreaId),
    retry: false,
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

      <section className="report-panel" aria-labelledby="coverage-gap-title">
        <div className="report-panel__header"><div><h2 id="coverage-gap-title">Coverage-gap analysis</h2><p>The part of the selected area's surveyed boundary that no in-scope camera's estimated coverage sector reaches.</p></div><StatusBadge tone="warning">Estimated</StatusBadge></div>
        {!submittedScope?.geographicAreaId && <p className="report-empty" role="status">Choose a geographic area above and load the coverage summary to also run gap analysis for it.</p>}
        {gaps.isPending && submittedScope?.geographicAreaId && <p role="status">Loading coverage-gap analysis…</p>}
        {gaps.isError && isApiProblem(gaps.error) && gaps.error.status === 404 && gaps.error.detail.includes('boundary') && <div role="status">
          <StatusBadge tone="neutral">No boundary set</StatusBadge>
          <p>This geographic area has no surveyed boundary polygon on file, so coverage cannot be measured against it. An administrator can load one with the geographic-area boundary import.</p>
        </div>}
        {gaps.isError && !(isApiProblem(gaps.error) && gaps.error.status === 404) && <p className="form-error" role="alert">{reportError(gaps.error)}</p>}
        {gaps.data && <div className="coverage-gap-result">
          <StatusBadge tone={gaps.data.geometry ? 'warning' : 'success'}>{gaps.data.geometry ? 'Gap found' : 'No gap found'}</StatusBadge>
          <p>{gaps.data.geometry
            ? 'Part of this area’s boundary is not reached by any in-scope camera’s estimated coverage sector.'
            : 'Every part of this area’s boundary is reached by at least one in-scope camera’s estimated coverage sector.'}</p>
          <dl>
            <div><dt>Camera sectors considered</dt><dd>{gaps.data.properties.cameraSectorsConsidered}</dd></div>
          </dl>
          <p className="report-empty">{gaps.data.properties.disclaimer}</p>
        </div>}
      </section>
      <section className="report-panel" aria-labelledby="ageing-title">
        <div className="report-panel__header"><div><h2 id="ageing-title">Ageing infrastructure</h2><p>In-scope, live cameras bucketed by years since installation — a planning aid for prioritising replacement.</p></div></div>
        {ageing.isPending ? <p role="status">Loading ageing-infrastructure report…</p>
          : ageing.isError ? <div><p className="form-error" role="alert">{reportError(ageing.error)}</p><button className="button" type="button" onClick={() => ageing.refetch()}>Retry ageing report</button></div>
            : <>
              <dl className="report-metrics">
                {ageing.data.buckets.map((bucket) => <div key={bucket.bucket}><dt>{bucket.label}</dt><dd>{bucket.bucket === '10_plus' && bucket.count > 0 ? <StatusBadge tone="warning">{bucket.count}</StatusBadge> : bucket.count}</dd></div>)}
              </dl>
              {ageing.data.oldestCameras.length === 0 ? <p className="report-empty">No camera has a recorded installation date yet.</p>
                : <div className="camera-table-wrap"><table className="camera-table"><caption className="sr-only">Oldest cameras by installation date</caption>
                  <thead><tr><th>Camera code</th><th>Name</th><th>Installed</th><th>Age</th><th>Maintenance status</th></tr></thead>
                  <tbody>{ageing.data.oldestCameras.map((camera) => <tr key={camera.id}>
                    <td><a href={`/cameras/${camera.id}`}>{camera.cameraCode}</a></td>
                    <td>{camera.name}</td>
                    <td>{camera.installationDate}</td>
                    <td>{camera.ageYears} year{camera.ageYears === 1 ? '' : 's'}</td>
                    <td>{camera.maintenanceStatus}</td>
                  </tr>)}</tbody>
                </table></div>}
            </>}
      </section>
    </section>
  );
}
