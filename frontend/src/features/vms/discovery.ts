import type { CameraWriteRequest, FederatedCameraResponse } from '../../api/models';
import { cameraTypes, roundCoordinate } from '../cameras/cameraVocabulary';

export interface DiscoveredCameraEnrichment {
  vmsId: string;
  cameraCode: string;
  name: string;
  organizationUnitId: string;
  siteId: string;
  cameraType: string;
  latitude: string;
  longitude: string;
  altitude: string;
  mountingHeight: string;
  azimuth: string;
  tilt: string;
  horizontalFov: string;
  verticalFov: string;
  effectiveRange: string;
}

export type DiscoveryEnrichmentErrors = Partial<Record<keyof DiscoveredCameraEnrichment, string>>;

const optionalNumberFields: Array<keyof Pick<DiscoveredCameraEnrichment,
  'altitude' | 'mountingHeight' | 'azimuth' | 'tilt' | 'horizontalFov' | 'verticalFov' | 'effectiveRange'>> = [
    'altitude', 'mountingHeight', 'azimuth', 'tilt', 'horizontalFov', 'verticalFov', 'effectiveRange',
  ];

function required(value: string, label: string, errors: DiscoveryEnrichmentErrors, field: keyof DiscoveredCameraEnrichment) {
  if (!value.trim()) errors[field] = `${label} is required.`;
}

function requiredText(value: string, label: string, maximum: number,
  errors: DiscoveryEnrichmentErrors, field: 'cameraCode' | 'name') {
  const trimmed = value.trim();
  if (!trimmed) errors[field] = `${label} is required.`;
  else if (trimmed.length > maximum) errors[field] = `${label} must be at most ${maximum} characters.`;
}

function mappedName(row: FederatedCameraResponse | undefined, enrichment: DiscoveredCameraEnrichment) {
  return enrichment.name.trim() || row?.name?.trim() || row?.nativeCameraId.trim() || '';
}

function validateCoordinate(value: string, label: string, minimum: number, maximum: number,
  errors: DiscoveryEnrichmentErrors, field: 'latitude' | 'longitude') {
  if (!value.trim()) {
    errors[field] = `${label} is required.`;
    return;
  }
  const number = Number(value);
  if (!Number.isFinite(number) || number < minimum || number > maximum) {
    errors[field] = `${label} must be between ${minimum} and ${maximum}.`;
  }
}

function validateOptionalNumber(value: string, label: string, minimum: number, maximum: number,
  errors: DiscoveryEnrichmentErrors, field: keyof DiscoveredCameraEnrichment) {
  if (!value.trim()) return;
  const number = Number(value);
  if (!Number.isFinite(number) || number < minimum || number > maximum) {
    errors[field] = `${label} must be between ${minimum} and ${maximum}.`;
  }
}

export function validateDiscoveredCameraEnrichment(
  enrichment: DiscoveredCameraEnrichment,
  row?: FederatedCameraResponse,
): DiscoveryEnrichmentErrors {
  const errors: DiscoveryEnrichmentErrors = {};
  requiredText(enrichment.cameraCode, 'Camera code', 100, errors, 'cameraCode');
  requiredText(mappedName(row, enrichment), 'Name', 255, errors, 'name');
  required(enrichment.organizationUnitId, 'Organization unit', errors, 'organizationUnitId');
  required(enrichment.siteId, 'Site', errors, 'siteId');
  if (!cameraTypes.includes(enrichment.cameraType as (typeof cameraTypes)[number])) errors.cameraType = 'Camera type is required.';
  validateCoordinate(enrichment.latitude, 'Latitude', -90, 90, errors, 'latitude');
  validateCoordinate(enrichment.longitude, 'Longitude', -180, 180, errors, 'longitude');
  validateOptionalNumber(enrichment.altitude, 'Altitude', -500, 9000, errors, 'altitude');
  validateOptionalNumber(enrichment.mountingHeight, 'Mounting height', 0, 200, errors, 'mountingHeight');
  validateOptionalNumber(enrichment.azimuth, 'Azimuth', 0, 359.999, errors, 'azimuth');
  validateOptionalNumber(enrichment.tilt, 'Tilt', -90, 90, errors, 'tilt');
  validateOptionalNumber(enrichment.horizontalFov, 'Horizontal field of view', 0.001, 360, errors, 'horizontalFov');
  validateOptionalNumber(enrichment.verticalFov, 'Vertical field of view', 0.001, 180, errors, 'verticalFov');
  validateOptionalNumber(enrichment.effectiveRange, 'Effective range', 0.001, 5000, errors, 'effectiveRange');
  return errors;
}

export function createDiscoveredCameraEnrichment(
  row: FederatedCameraResponse,
  vmsId: string,
  vmsCode: string,
): DiscoveredCameraEnrichment {
  return {
    vmsId,
    cameraCode: `${vmsCode}-${row.nativeCameraId}`,
    name: row.name?.trim() || row.nativeCameraId,
    organizationUnitId: '',
    siteId: '',
    cameraType: '',
    latitude: '',
    longitude: '',
    altitude: '',
    mountingHeight: '',
    azimuth: '',
    tilt: '',
    horizontalFov: '',
    verticalFov: '',
    effectiveRange: '',
  };
}

export function toDiscoveredCameraWriteRequest(
  row: FederatedCameraResponse,
  enrichment: DiscoveredCameraEnrichment,
): CameraWriteRequest {
  const errors = validateDiscoveredCameraEnrichment(enrichment, row);
  const firstError = Object.values(errors)[0];
  if (firstError) throw new Error(firstError);

  const request: CameraWriteRequest = {
    cameraCode: enrichment.cameraCode.trim(),
    name: mappedName(row, enrichment),
    organizationUnitId: enrichment.organizationUnitId,
    siteId: enrichment.siteId,
    cameraType: enrichment.cameraType,
    latitude: roundCoordinate(Number(enrichment.latitude)),
    longitude: roundCoordinate(Number(enrichment.longitude)),
    vmsId: enrichment.vmsId,
  };

  const model = row.vendorModel?.trim();
  if (model) request.model = model;
  const streamReference = row.streamReferences.find((reference) => reference.trim())?.trim();
  if (streamReference) request.streamReference = streamReference;
  optionalNumberFields.forEach((field) => {
    const value = enrichment[field].trim();
    if (value) request[field] = Number(value);
  });

  return request;
}
