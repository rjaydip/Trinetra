import { z } from 'zod';

import type { BulkImportRequest, CameraWriteRequest } from '../../api/models';

const nullableString = z.string().nullable().optional();
const nullableNumber = z.number().finite().nullable().optional();
const nullableUuid = z.guid({ error: 'VMS ID must be a valid UUID.' }).nullable().optional();
const nullableDateOnly = z.iso.date({ error: 'Installation date must use the YYYY-MM-DD format.' }).nullable().optional();

// This is deliberately a transport-shape check rather than a duplicate of server-side camera
// validation. The API's BulkImportResult is the authoritative per-row validation report.
const importItemSchema = z.object({
  cameraCode: z.string().optional(), name: z.string().optional(), organizationUnitId: z.string().optional(),
  siteId: z.string().optional(), cameraType: z.string().optional(), latitude: z.number().finite().optional(), longitude: z.number().finite().optional(),
  manufacturer: nullableString, model: nullableString, serialNumber: nullableString, altitude: nullableNumber,
  mountingHeight: nullableNumber, azimuth: nullableNumber, tilt: nullableNumber, horizontalFov: nullableNumber,
  verticalFov: nullableNumber, effectiveRange: nullableNumber, ipAddress: nullableString, port: z.number().int().nullable().optional(),
  protocol: nullableString, vmsId: nullableUuid, streamReference: nullableString, credentialReference: nullableString,
  installationDate: nullableDateOnly, operationalStatus: nullableString, connectivityStatus: nullableString, maintenanceStatus: nullableString,
}).strict();

const importEnvelopeSchema = z.object({
  mode: z.enum(['insert', 'upsert']),
  items: z.array(importItemSchema).min(1, 'Import items must contain between 1 and 500 items.').max(500, 'Import items must contain between 1 and 500 items.'),
}).strict();

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
