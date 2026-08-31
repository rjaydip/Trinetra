import { describe, expect, it } from 'vitest';

import type { CameraPatchRequest } from './models';

const patchThatClearsNullableFields: CameraPatchRequest = {
  latitude: 23.0225,
  manufacturer: null,
};

// @ts-expect-error Camera codes are immutable and must not be accepted by PATCH.
const patchWithImmutableCameraCode: CameraPatchRequest = { cameraCode: 'CAM-AHM-001' };

describe('CameraPatchRequest', () => {
  it('preserves nullable field clearing in a valid patch payload', () => {
    expect(patchThatClearsNullableFields).toEqual({ latitude: 23.0225, manufacturer: null });
    expect(patchWithImmutableCameraCode).toEqual({ cameraCode: 'CAM-AHM-001' });
  });
});
