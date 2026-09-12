import { describe, expect, it } from 'vitest';

import { createSampleImport } from './import';

describe('createSampleImport', () => {
  it('creates an insert JSON template containing only contract fields', () => {
    expect(createSampleImport()).toEqual({
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
    });
  });
});
