import { describe, expect, it } from 'vitest';

import type { FederatedCameraResponse } from '../../api/models';
import { createDiscoveredCameraEnrichment, toDiscoveredCameraWriteRequest, validateDiscoveredCameraEnrichment, type DiscoveredCameraEnrichment } from './discovery';

const discovered: FederatedCameraResponse = {
  nativeCameraId: 'CAM-07',
  cameraId: null,
  name: 'Gate 7',
  vendorModel: 'IPC-HFW1230S',
  firmware: '2.800.0000000.8.R',
  isEnabled: true,
  isRecording: true,
  health: 'ONLINE',
  lastSeen: '2026-09-04T10:00:00Z',
  streamReferences: ['rtsp://stream/7', 'rtsp://stream/7/sub'],
  statusChangedAt: '2026-09-04T09:30:00Z',
};

const enrichment: DiscoveredCameraEnrichment = {
  vmsId: '44444444-4444-4444-8444-444444444444',
  cameraCode: 'NVR-001-CAM-07',
  name: 'Gate 7',
  organizationUnitId: '22222222-2222-4222-8222-222222222222',
  siteId: '33333333-3333-4333-8333-333333333333',
  cameraType: 'FIXED',
  latitude: '19.076012345',
  longitude: '72.877700049',
  altitude: '',
  mountingHeight: '4.5',
  azimuth: '90',
  tilt: '',
  horizontalFov: '82.5',
  verticalFov: '',
  effectiveRange: '45',
};

describe('toDiscoveredCameraWriteRequest', () => {
  it('maps a selected discovered camera to an upsert-ready write request', () => {
    expect(toDiscoveredCameraWriteRequest(discovered, enrichment)).toEqual({
      cameraCode: 'NVR-001-CAM-07',
      name: 'Gate 7',
      organizationUnitId: '22222222-2222-4222-8222-222222222222',
      siteId: '33333333-3333-4333-8333-333333333333',
      cameraType: 'FIXED',
      latitude: 19.0760123,
      longitude: 72.8777,
      model: 'IPC-HFW1230S',
      mountingHeight: 4.5,
      azimuth: 90,
      horizontalFov: 82.5,
      effectiveRange: 45,
      vmsId: '44444444-4444-4444-8444-444444444444',
      streamReference: 'rtsp://stream/7',
    });
  });

  it('falls back to the native camera id when the discovery name is absent', () => {
    expect(toDiscoveredCameraWriteRequest(
      { ...discovered, name: null, vendorModel: null, streamReferences: [] },
      { ...enrichment, name: '' },
    )).toEqual(expect.objectContaining({ name: 'CAM-07' }));
  });

  it('rejects invalid optional coverage values before mapping them into the request', () => {
    expect(validateDiscoveredCameraEnrichment({ ...enrichment, azimuth: '360' })).toEqual(expect.objectContaining({
      azimuth: 'Azimuth must be between 0 and 359.999.',
    }));
  });

  it('rejects an overlong generated camera code before mapping', () => {
    const generated = createDiscoveredCameraEnrichment(discovered, enrichment.vmsId, 'N'.repeat(94));
    const completed = { ...generated, organizationUnitId: enrichment.organizationUnitId, siteId: enrichment.siteId, cameraType: 'FIXED', latitude: '19', longitude: '72' };

    expect(() => toDiscoveredCameraWriteRequest(discovered, completed)).toThrow('Camera code must be at most 100 characters.');
  });

  it('rejects overlong edited camera code and name values', () => {
    expect(validateDiscoveredCameraEnrichment({ ...enrichment, cameraCode: 'C'.repeat(101), name: 'N'.repeat(256) })).toEqual(expect.objectContaining({
      cameraCode: 'Camera code must be at most 100 characters.',
      name: 'Name must be at most 255 characters.',
    }));
  });

  it('rejects an empty final camera code and name', () => {
    expect(validateDiscoveredCameraEnrichment(
      { ...enrichment, cameraCode: ' ', name: ' ' },
      { ...discovered, nativeCameraId: ' ', name: null },
    )).toEqual(expect.objectContaining({
      cameraCode: 'Camera code is required.',
      name: 'Name is required.',
    }));
  });
});
