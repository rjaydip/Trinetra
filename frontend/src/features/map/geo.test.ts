import { describe, expect, it } from 'vitest';

import { buildMapRequest, initialBoundsFromCameras } from './geo';
import { cameraFixture } from '../../test/fixtures';

describe('buildMapRequest', () => {
  it('does not request a GIS feed wider than the API maximum', () => {
    expect(buildMapRequest([-76, 39, -70, 45], {})).toBeNull();
  });

  it.each([
    [-181, 0, -180, 1],
    [179, 0, 181, 1],
    [0, -91, 1, -90],
    [0, 89, 1, 91],
  ] as const)('rejects out-of-range geographic coordinates: %s,%s,%s,%s', (west, south, east, north) => {
    expect(buildMapRequest([west, south, east, north], {})).toBeNull();
  });
});

describe('live bootstrap bounds', () => {
  it('includes an actual camera when the registry spans distant regions', () => {
    const cameras = [cameraFixture({ longitude: -120, latitude: 35 }), cameraFixture({ longitude: 120, latitude: -35 })];
    const bounds = initialBoundsFromCameras(cameras)!;
    expect(cameras.some((camera) => camera.longitude >= bounds[0] && camera.longitude <= bounds[2] && camera.latitude >= bounds[1] && camera.latitude <= bounds[3])).toBe(true);
    expect(buildMapRequest(bounds, {})).not.toBeNull();
  });

  it.each([[180, 90], [-180, -90], [179.999, 89.999], [-179.999, -89.999]])('keeps boundary camera %s,%s inside valid request bounds', (longitude, latitude) => {
    const bounds = initialBoundsFromCameras([cameraFixture({ longitude, latitude })])!;
    expect(buildMapRequest(bounds, {})).not.toBeNull();
    expect(bounds[0]).toBeLessThanOrEqual(longitude);
    expect(bounds[2]).toBeGreaterThanOrEqual(longitude);
    expect(bounds[1]).toBeLessThanOrEqual(latitude);
    expect(bounds[3]).toBeGreaterThanOrEqual(latitude);
  });

  it('skips invalid coordinates before selecting a live target', () => {
    const bounds = initialBoundsFromCameras([cameraFixture({ longitude: 500, latitude: 92 }), cameraFixture({ longitude: 10, latitude: 20 })])!;
    expect(buildMapRequest(bounds, {})).not.toBeNull();
    expect(bounds[0]).toBeLessThan(10);
    expect(bounds[2]).toBeGreaterThan(10);
    expect(bounds[1]).toBeLessThan(20);
    expect(bounds[3]).toBeGreaterThan(20);
  });
});
