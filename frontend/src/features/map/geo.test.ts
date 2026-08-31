import { describe, expect, it } from 'vitest';

import { buildMapRequest } from './geo';

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
