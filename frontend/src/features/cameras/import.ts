import { z } from 'zod';

import { api } from '../../api/endpoints';
import type { BulkImportRequest, CameraResponse, CameraWriteRequest, GeographicAreaResponse, OrganizationUnitResponse, SavedCredentialResponse } from '../../api/models';

const nullableString = z.string().nullable().optional();
const nullableNumber = z.number().finite().nullable().optional();
const nullableUuid = z.guid({ error: 'VMS ID must be a valid UUID.' }).nullable().optional();
const nullableDateOnly = z.iso.date({ error: 'Installation date must use the YYYY-MM-DD format.' }).nullable().optional();

// This is deliberately a transport-shape check rather than a duplicate of server-side camera
// validation. The API's BulkImportResult is the authoritative per-row validation report.
const importItemSchema = z.object({
  cameraCode: z.string().optional(), name: z.string().optional(), organizationUnitId: z.string().optional(),
  geographicAreaId: z.string().optional(), cameraType: z.string().optional(), latitude: z.number().finite().optional(), longitude: z.number().finite().optional(),
  manufacturer: nullableString, model: nullableString, serialNumber: nullableString, altitude: nullableNumber,
  mountingHeight: nullableNumber, azimuth: nullableNumber, tilt: nullableNumber, horizontalFov: nullableNumber,
  verticalFov: nullableNumber, effectiveRange: nullableNumber, ipAddress: nullableString, port: z.number().int().nullable().optional(),
  protocol: nullableString, vmsId: nullableUuid, streamReference: nullableString, credentialReference: nullableString,
  streamPreference: z.string().optional(), nativeHlsUrl: nullableString, nativeWebrtcUrl: nullableString,
  installationDate: nullableDateOnly, operationalStatus: nullableString, connectivityStatus: nullableString, maintenanceStatus: nullableString,
  recordEvents: z.boolean().optional(),
}).strict();

const importEnvelopeSchema = z.object({
  mode: z.enum(['insert', 'upsert']),
  items: z.array(importItemSchema).min(1, 'Import items must contain between 1 and 500 items.').max(500, 'Import items must contain between 1 and 500 items.'),
}).strict();

export function createSampleImport(): BulkImportRequest {
  return {
    mode: 'insert',
    items: [{
      cameraCode: 'CAM-EXAMPLE-001',
      name: 'Example camera',
      organizationUnitId: '00000000-0000-0000-0000-000000000001',
      geographicAreaId: '00000000-0000-0000-0000-000000000002',
      cameraType: 'FIXED',
      latitude: 19.076,
      longitude: 72.8777,
    }],
  };
}

export function parseBulkImport(text: string): BulkImportRequest {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error('The selected file is not valid JSON.');
  }
  const result = importEnvelopeSchema.safeParse(parsed);
  if (!result.success) throw new Error(result.error.issues[0]?.message ?? 'The JSON import request is invalid.');
  return { mode: result.data.mode, items: result.data.items as CameraWriteRequest[] };
}

// ===========================================================================
// CSV import/export
// ===========================================================================
//
// A CSV file is edited by people, not machines — an organization unit, geographic area, or saved
// credential's opaque UUID means nothing to someone filling in a spreadsheet. So the CSV format
// uses each one's human-readable *name* instead (`organizationUnit`, `geographicArea`,
// `credential` columns), and resolves names to the IDs the API actually needs at parse time,
// against a name/id lookup built from the registry's own current organization units, geographic
// areas, and saved-credential library (`ImportReferenceData` / `loadImportReferenceData`).

/** Name/id lookups (both directions) for the three CSV columns that reference another record by
 * name instead of by ID — built once per import/export session (`loadImportReferenceData`) and
 * threaded through every CSV read/write in this module rather than re-fetched per row. A name
 * that isn't unique (two organization units in different parts of the hierarchy sharing a name,
 * say) is deliberately treated as unresolvable rather than guessed at — silently picking one would
 * risk scoping a camera to the wrong department, which is worse than asking the person to
 * disambiguate it themselves. */
export interface ImportReferenceData {
  organizationUnitIdByName: Map<string, string>;
  organizationUnitNameById: Map<string, string>;
  geographicAreaIdByName: Map<string, string>;
  geographicAreaNameById: Map<string, string>;
  credentialReferenceByName: Map<string, string>;
  credentialNameByReference: Map<string, string>;
}

function buildBidirectionalMaps(entries: Array<{ id: string; name: string }>): { idByName: Map<string, string>; nameById: Map<string, string> } {
  const nameCounts = new Map<string, number>();
  for (const entry of entries) {
    const key = entry.name.trim().toLowerCase();
    nameCounts.set(key, (nameCounts.get(key) ?? 0) + 1);
  }

  const idByName = new Map<string, string>();
  const nameById = new Map<string, string>();
  for (const entry of entries) {
    const key = entry.name.trim().toLowerCase();
    // A duplicate name is left out of idByName entirely (not "first match wins") — see the
    // interface doc comment on why an ambiguous name must fail to resolve rather than guess.
    if (nameCounts.get(key) === 1) idByName.set(key, entry.id);
    // nameById has no such ambiguity — every id is unique by construction, so this direction is
    // always safe to populate, even for a duplicated display name.
    nameById.set(entry.id, entry.name);
  }
  return { idByName, nameById };
}

export function buildImportReferenceData(
  organizationUnits: OrganizationUnitResponse[],
  geographicAreas: GeographicAreaResponse[],
  credentials: SavedCredentialResponse[],
): ImportReferenceData {
  const units = buildBidirectionalMaps(organizationUnits);
  const areas = buildBidirectionalMaps(geographicAreas);
  const creds = buildBidirectionalMaps(credentials.map((c) => ({ id: c.credentialReference, name: c.name })));
  return {
    organizationUnitIdByName: units.idByName, organizationUnitNameById: units.nameById,
    geographicAreaIdByName: areas.idByName, geographicAreaNameById: areas.nameById,
    credentialReferenceByName: creds.idByName, credentialNameByReference: creds.nameById,
  };
}

/** Fetches everything `buildImportReferenceData` needs. Organization units are only ever listed
 * per-organization (`api.reference.organizationUnits`), so this fans out across every
 * organization first — a CSV row names a unit without saying which organization it belongs to,
 * so resolving it requires the whole cross-organization set built once up front. */
export async function loadImportReferenceData(signal?: AbortSignal): Promise<ImportReferenceData> {
  const organizations = await api.reference.organizations(signal);
  const [unitLists, geographicAreas, credentials] = await Promise.all([
    Promise.all(organizations.map((organization) => api.reference.organizationUnits(organization.id, signal))),
    api.reference.geographicAreas(undefined, signal),
    api.credentialLibrary.list(signal),
  ]);
  return buildImportReferenceData(unitLists.flat(), geographicAreas, credentials);
}

/** CSV column order, both directions — `cameraWriteRequestsToCsv`/`createSampleCsv` write these
 * headers, `parseCsvBulkImport` reads them by name (not position). Distinct from
 * `CameraWriteRequest`'s own keys in exactly three places: `organizationUnit`, `geographicArea`,
 * and `credential` hold names, not the `organizationUnitId`/`geographicAreaId`/
 * `credentialReference` IDs those names resolve to (see the module doc comment above). `mode` is
 * deliberately not a column: it applies to the whole batch, not one row — the Bulk Import page's
 * own "Insert" / "Upsert" radio covers that once a file loads, same as for a parsed JSON
 * envelope. */
const csvColumns = [
  'cameraCode', 'name', 'organizationUnit', 'geographicArea', 'cameraType', 'latitude', 'longitude',
  'manufacturer', 'model', 'serialNumber', 'altitude', 'mountingHeight', 'azimuth', 'tilt', 'horizontalFov',
  'verticalFov', 'effectiveRange', 'ipAddress', 'port', 'protocol', 'vmsId', 'streamReference',
  'credential', 'streamPreference', 'nativeHlsUrl', 'nativeWebrtcUrl', 'installationDate',
  'operationalStatus', 'connectivityStatus', 'maintenanceStatus', 'recordEvents',
] as const;

type CsvColumn = (typeof csvColumns)[number];

const numericCsvColumns = new Set<CsvColumn>([
  'latitude', 'longitude', 'altitude', 'mountingHeight', 'azimuth', 'tilt', 'horizontalFov', 'verticalFov',
  'effectiveRange', 'port',
]);

/** The three name-based columns and the `CameraWriteRequest` id field each resolves to. */
const relationalColumns: Record<'organizationUnit' | 'geographicArea' | 'credential', keyof CameraWriteRequest> = {
  organizationUnit: 'organizationUnitId',
  geographicArea: 'geographicAreaId',
  credential: 'credentialReference',
};

/** Quotes a CSV field only when it needs it (contains a comma, quote, or newline) — an unquoted
 * field round-trips through any ordinary spreadsheet program unchanged, which quoting every field
 * would not. Embedded quotes are doubled, the RFC 4180 escape. */
function csvField(value: string): string {
  return /[",\n\r]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

function csvCell(item: CameraWriteRequest, column: CsvColumn, reference: ImportReferenceData): string {
  if (column === 'organizationUnit') return csvField(item.organizationUnitId ? reference.organizationUnitNameById.get(item.organizationUnitId) ?? '' : '');
  if (column === 'geographicArea') return csvField(item.geographicAreaId ? reference.geographicAreaNameById.get(item.geographicAreaId) ?? '' : '');
  if (column === 'credential') return csvField(item.credentialReference ? reference.credentialNameByReference.get(item.credentialReference) ?? '' : '');

  const value = item[column];
  if (value === null || value === undefined) return '';
  return csvField(String(value));
}

/** Serializes camera rows to the CSV format `parseCsvBulkImport` reads back — used both for the
 * downloadable blank template and for "download 5 existing cameras as a starting point," so both
 * paths produce exactly the same shape a real import expects. Requires `reference` (built by
 * `loadImportReferenceData`) so the organization unit / geographic area / credential columns can
 * be written as names, not raw IDs. */
export function cameraWriteRequestsToCsv(items: CameraWriteRequest[], reference: ImportReferenceData): string {
  const lines = [csvColumns.join(',')];
  for (const item of items) {
    lines.push(csvColumns.map((column) => csvCell(item, column, reference)).join(','));
  }
  // CRLF: the conventional CSV line ending, and what makes the file open predictably in Excel
  // rather than risking it treating the whole file as one line.
  return lines.join('\r\n') + '\r\n';
}

/** A blank CSV template — one example row. The organization unit / geographic area / credential
 * columns are left blank rather than filled with a fake name: unlike the JSON sample's made-up
 * UUIDs (which only ever need to look syntactically valid), a CSV name gets looked up against the
 * real registry, and a made-up name would just resolve to nothing. */
export function createSampleCsv(): string {
  const sample = createSampleImport().items[0];
  const header = csvColumns.join(',');
  const row = csvColumns.map((column) => {
    if (column in relationalColumns) return '';
    const value = sample[column as keyof CameraWriteRequest];
    return value === null || value === undefined ? '' : csvField(String(value));
  }).join(',');
  return `${header}\r\n${row}\r\n`;
}

/** Fields this app doesn't collect and so should never round-trip into a re-importable template:
 * sealed credentials (never re-readable — `credentialReference` is what's reused instead), and
 * server-computed status/lifecycle fields a bulk import shouldn't be dictating. */
function toWritableCameraRow(camera: CameraResponse): CameraWriteRequest {
  const { id: _id, hasCoverage: _hasCoverage, lastSeenAt: _lastSeenAt, lastHealthCheckAt: _lastHealthCheckAt,
    retiredAt: _retiredAt, ...rest } = camera;
  return rest;
}

/** Real existing cameras (up to `count`), in the same CSV shape `parseCsvBulkImport` reads — the
 * "download existing cameras as a sample" template, so a new import can be built by editing real,
 * valid rows rather than guessing values from scratch. */
export function camerasToSampleCsv(cameras: CameraResponse[], reference: ImportReferenceData, count = 5): string {
  return cameraWriteRequestsToCsv(cameras.slice(0, count).map(toWritableCameraRow), reference);
}

/** Every camera in `cameras` (no count cap, unlike `camerasToSampleCsv`) — the RFP "role-based
 * search/filter/export" deliverable: whatever a caller's current registry search/filter matched,
 * exported in the same shape a CSV bulk import reads back, so an export is also always a valid
 * starting point for a re-import. */
export function exportCamerasToCsv(cameras: CameraResponse[], reference: ImportReferenceData): string {
  return cameraWriteRequestsToCsv(cameras.map(toWritableCameraRow), reference);
}

/** One row's unresolved organization-unit / geographic-area / credential name — the CSV importer
 * doesn't fail the whole file over this (see `parseCsvBulkImport`'s doc comment), so these are
 * surfaced to the person instead, as something to go fix and re-upload. */
export interface CsvRowWarning {
  row: number;
  column: 'organizationUnit' | 'geographicArea' | 'credential';
  value: string;
  message: string;
}

/** Minimal RFC 4180 field/row splitter: handles quoted fields containing commas, embedded
 * newlines, and doubled-quote escapes — enough for what a spreadsheet program actually writes.
 * Not a streaming parser; the whole file is read into memory first, matching `parseBulkImport`'s
 * JSON path and the bulk-import row cap (500) this feeds into. */
function parseCsvRows(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let field = '';
  let inQuotes = false;
  // Normalizes CRLF/CR to LF up front so the single-character scan below never has to special-case
  // a two-character line ending.
  const normalized = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');

  for (let i = 0; i < normalized.length; i++) {
    const char = normalized[i];
    if (inQuotes) {
      if (char === '"') {
        if (normalized[i + 1] === '"') {
          field += '"';
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        field += char;
      }
      continue;
    }

    if (char === '"') {
      inQuotes = true;
    } else if (char === ',') {
      row.push(field);
      field = '';
    } else if (char === '\n') {
      row.push(field);
      rows.push(row);
      row = [];
      field = '';
    } else {
      field += char;
    }
  }

  // The final field/row has no trailing delimiter to trigger the push above.
  if (field.length > 0 || row.length > 0) {
    row.push(field);
    rows.push(row);
  }

  return rows.filter((candidate) => !(candidate.length === 1 && candidate[0] === ''));
}

function coerceCsvValue(column: CsvColumn, raw: string): unknown {
  const trimmed = raw.trim();
  if (trimmed === '') return undefined;
  if (column === 'recordEvents') return trimmed.toLowerCase() === 'true';
  if (numericCsvColumns.has(column)) {
    const parsed = Number(trimmed);
    // Left as the original string (not `undefined`) when unparseable, so `importItemSchema`'s own
    // type check rejects it with a specific "must be a number" message pointing at the row, rather
    // than the bad value silently vanishing as if the cell had been left blank.
    return Number.isFinite(parsed) ? parsed : trimmed;
  }
  return trimmed;
}

/**
 * Parses a CSV file in the format `cameraWriteRequestsToCsv` writes into a `BulkImportRequest`,
 * resolving the `organizationUnit` / `geographicArea` / `credential` name columns against
 * `reference` (see `loadImportReferenceData`). Columns are matched by header name, not position,
 * so a template edited in a spreadsheet program (columns reordered, extra columns removed) still
 * parses; an unrecognized column name is rejected outright rather than silently ignored, the same
 * "fail loud on shape drift" posture `importItemSchema`'s `.strict()` already takes for JSON.
 *
 * A name that doesn't resolve — misspelled, or removed since the CSV was made — does *not* fail
 * the whole file. Per-row/per-field type errors (a non-numeric latitude, a malformed VMS UUID)
 * still throw immediately, because there's no sensible partial result to show for those. An
 * unresolved *name*, on the other hand, is a routine, expected thing to happen when editing a
 * spreadsheet by hand: that field is left unset in the returned request (the server's own "field
 * is required" reports it again if it matters) and recorded as a `CsvRowWarning` instead, so the
 * whole file still loads for preview and the person can see exactly which cells to go fix, rather
 * than the file being rejected outright over one bad row out of hundreds.
 */
export function parseCsvBulkImport(
  text: string, reference: ImportReferenceData,
): { request: BulkImportRequest; warnings: CsvRowWarning[] } {
  const rows = parseCsvRows(text);
  if (rows.length === 0) {
    throw new Error('The selected file is empty.');
  }

  const header = rows[0].map((column) => column.trim());
  const unknownColumns = header.filter((column) => !csvColumns.includes(column as CsvColumn));
  if (unknownColumns.length > 0) {
    throw new Error(`Unrecognized column(s) in the CSV header: ${unknownColumns.join(', ')}.`);
  }

  const dataRows = rows.slice(1);
  if (dataRows.length === 0) {
    throw new Error('The CSV file has a header row but no camera rows.');
  }
  if (dataRows.length > 500) {
    throw new Error('Import items must contain between 1 and 500 items.');
  }

  const warnings: CsvRowWarning[] = [];

  const items = dataRows.map((cells, rowIndex) => {
    const rowNumber = rowIndex + 2; // header is row 1; spreadsheet programs are 1-indexed.
    const raw: Record<string, unknown> = {};
    const relationalNames: Partial<Record<keyof typeof relationalColumns, string>> = {};

    header.forEach((column, columnIndex) => {
      const cell = (cells[columnIndex] ?? '').trim();
      if (column === 'organizationUnit' || column === 'geographicArea' || column === 'credential') {
        if (cell) relationalNames[column] = cell;
        return;
      }
      const value = coerceCsvValue(column as CsvColumn, cell);
      if (value !== undefined) raw[column] = value;
    });

    const result = importItemSchema.safeParse(raw);
    if (!result.success) {
      throw new Error(`Row ${rowNumber}: ${result.error.issues[0]?.message ?? 'invalid value.'}`);
    }

    const item: Record<string, unknown> = { ...result.data };
    for (const [column, idField] of Object.entries(relationalColumns) as Array<[keyof typeof relationalColumns, keyof CameraWriteRequest]>) {
      const rawName = relationalNames[column];
      if (!rawName) continue;

      const idByName = column === 'organizationUnit' ? reference.organizationUnitIdByName
        : column === 'geographicArea' ? reference.geographicAreaIdByName
          : reference.credentialReferenceByName;
      const resolved = idByName.get(rawName.toLowerCase());
      if (resolved) {
        item[idField] = resolved;
      } else {
        warnings.push({
          row: rowNumber, column, value: rawName,
          message: `Row ${rowNumber}: "${rawName}" did not match exactly one known ${columnLabel(column)} — left blank. Please correct it.`,
        });
      }
    }

    // item is built incrementally (schema-validated fields, then resolved relational ids) rather
    // than constructed as a CameraWriteRequest literal, so TS can't see the two shapes converge —
    // they do, by construction: every key set above is a real CameraWriteRequest key.
    return item as unknown as CameraWriteRequest;
  });

  return { request: { mode: 'insert', items }, warnings };
}

function columnLabel(column: keyof typeof relationalColumns): string {
  if (column === 'organizationUnit') return 'organization unit';
  if (column === 'geographicArea') return 'geographic area';
  return 'saved credential';
}
