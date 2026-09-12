import { afterEach, describe, expect, it, vi } from 'vitest';

import { api } from './endpoints';

const targetId = '11111111-1111-4111-8111-111111111111';
const cameraId = '22222222-2222-4222-8222-222222222222';

function respondWithJson(): void {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({})));
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('reconciliation API client', () => {
  it('lists the unreconciled backlog, optionally filtered by target', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ items: [], nextCursor: null })));

    await api.reconciliation.unreconciled({ targetId });

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/cameras/unreconciled?targetId=${targetId}`),
      expect.anything(),
    );
  });

  it('links a registry camera to a VMS-reported camera', async () => {
    respondWithJson();

    await api.reconciliation.reconcile(cameraId, {
      targetId, nativeCameraId: 'CAM-07', adoptStreamReference: true, adoptVmsId: true,
    });

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/cameras/${cameraId}/reconcile`),
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ targetId, nativeCameraId: 'CAM-07', adoptStreamReference: true, adoptVmsId: true }),
      }),
    );
  });

  it('registers a camera from an unreconciled federated row and links it in one call', async () => {
    respondWithJson();

    await api.reconciliation.createFromFederated({
      targetId, nativeCameraId: 'CAM-07', cameraCode: 'NVR-001-CAM-07', cameraType: 'FIXED',
    });

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining('/api/v1/cameras/from-federated'),
      expect.objectContaining({ method: 'POST' }),
    );
  });
});
