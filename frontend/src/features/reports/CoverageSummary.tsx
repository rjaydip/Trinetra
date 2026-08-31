import type { CoverageSummaryResponse } from '../../api/models';

export function CoverageSummary({ coverage }: { coverage: CoverageSummaryResponse }) {
  const dimensions = Object.entries(coverage.buckets);

  if (dimensions.length === 0) {
    return <p className="report-empty" role="status">No coverage buckets were returned for this area.</p>;
  }

  return (
    <div className="coverage-summary">
      {dimensions.map(([dimension, buckets]) => (
        <section key={dimension} aria-labelledby={`coverage-${dimension}`} className="coverage-summary__bucket">
          <h3 id={`coverage-${dimension}`}>{dimension}</h3>
          {Object.keys(buckets).length === 0 ? <p className="report-empty" role="status">No values were returned.</p> : (
            <dl>
              {Object.entries(buckets).map(([label, count]) => (
                <div key={label}><dt>{label}</dt><dd>{count}</dd></div>
              ))}
            </dl>
          )}
        </section>
      ))}
    </div>
  );
}
