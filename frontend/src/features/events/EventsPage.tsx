import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import { PageState, StatusBadge } from '../../components/ui';

function errorDetail(error: unknown, fallback: string) {
  return isApiProblem(error) ? error.detail : fallback;
}

function toDateTimeLocal(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function isoOrNull(value: string): string | null {
  if (!value) return null;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.toISOString();
}

const defaultTo = () => toDateTimeLocal(new Date());
const defaultFrom = () => toDateTimeLocal(new Date(Date.now() - 60 * 60 * 1000));

/**
 * `GET /api/v1/events` — the hot PostgreSQL window only (7 days max, `from`/`to` required and
 * never defaulted by the API itself — a missing range becoming "everything" is the query that
 * takes the database down). This page supplies a sensible default window instead.
 */
export function EventsPage() {
  const [from, setFrom] = useState(defaultFrom);
  const [to, setTo] = useState(defaultTo);
  const [cameraId, setCameraId] = useState('');
  const [eventType, setEventType] = useState('');
  const [objectReference, setObjectReference] = useState('');
  const [cursor, setCursor] = useState<string | undefined>(undefined);

  const fromIso = isoOrNull(from);
  const toIso = isoOrNull(to);
  const rangeValid = Boolean(fromIso && toIso && fromIso < toIso);

  const events = useQuery({
    queryKey: ['events', fromIso, toIso, cameraId, eventType, objectReference, cursor],
    queryFn: () => api.events.query({
      from: fromIso!, to: toIso!,
      cameraId: cameraId.trim() || undefined,
      eventType: eventType.trim() || undefined,
      objectReference: objectReference.trim() || undefined,
      cursor,
    }),
    enabled: rangeValid,
  });

  function updateFilter(setter: (value: string) => void, value: string) {
    setter(value);
    setCursor(undefined);
  }

  return <section className="events-page" aria-labelledby="events-title">
    <header>
      <p className="eyebrow">Federation</p>
      <h1 id="events-title">Events</h1>
      <p>Normalized events from every federated VMS, whatever vendor produced them — the hot window only, at most 7 days.</p>
    </header>
    <fieldset className="registry-filters">
      <legend>Search filters</legend>
      <label>From<span aria-hidden="true"> *</span><input aria-required="true" type="datetime-local" value={from} onChange={(event) => updateFilter(setFrom, event.target.value)} /></label>
      <label>To<span aria-hidden="true"> *</span><input aria-required="true" type="datetime-local" value={to} onChange={(event) => updateFilter(setTo, event.target.value)} /></label>
      <label>Camera id<input value={cameraId} onChange={(event) => updateFilter(setCameraId, event.target.value)} /></label>
      <label>Event type<input value={eventType} onChange={(event) => updateFilter(setEventType, event.target.value)} /></label>
      <label>Object reference<input value={objectReference} onChange={(event) => updateFilter(setObjectReference, event.target.value)} /></label>
    </fieldset>
    {!rangeValid ? <PageState title="Invalid time range">'To' must be after 'From'.</PageState>
      : events.isPending ? <PageState title="Loading events">Retrieving matching events…</PageState>
        : events.isError ? <><PageState title="Couldn&apos;t load events">{errorDetail(events.error, 'Events could not be loaded.')}</PageState><button className="button" type="button" onClick={() => events.refetch()}>Try again</button></>
          : events.data.events.length === 0 ? <PageState title="No events">No events matched this search.</PageState>
            : <div className="camera-table-wrap"><table className="camera-table"><caption>{events.data.events.length} event{events.data.events.length === 1 ? '' : 's'}</caption><thead><tr>
              <th scope="col">Occurred at</th><th scope="col">Type</th><th scope="col">Vendor type</th><th scope="col">Camera</th><th scope="col">Severity</th><th scope="col">Object</th>
            </tr></thead><tbody>{events.data.events.map((event) => <tr key={event.eventId}>
              <td>{new Date(event.occurredAt).toLocaleString()}</td>
              <td>{event.eventType}</td>
              <td>{event.vendorEventType ?? 'Not reported'}</td>
              <td>{event.cameraId}</td>
              <td><StatusBadge tone={event.severity.toLowerCase() === 'critical' || event.severity.toLowerCase() === 'high' ? 'danger' : event.severity.toLowerCase() === 'medium' ? 'warning' : 'neutral'}>{event.severity}</StatusBadge></td>
              <td>{event.objectReference ?? 'Not reported'}</td>
            </tr>)}</tbody></table></div>}
    {rangeValid && events.isSuccess && <nav aria-label="Event pagination" className="registry-pagination">
      <span aria-live="polite">{events.data.nextCursor ? 'More events are available.' : 'End of available events.'}</span>
      <button className="button" disabled={events.isFetching || !events.data.nextCursor} type="button" onClick={() => setCursor(events.data.nextCursor ?? undefined)}>Next</button>
    </nav>}
  </section>;
}
