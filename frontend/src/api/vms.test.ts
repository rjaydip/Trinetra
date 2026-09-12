import { afterEach, describe, expect, it, vi } from 'vitest';

import { api } from './endpoints';
import { apiBaseUrl } from './client';
import { saveSession } from '../auth/session';
import { sessionFixture } from '../test/fixtures';

const vmsId = '11111111-1111-4111-8111-111111111111';
const testId = '22222222-2222-4222-8222-222222222222';

function respondWithJson(): void {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({})));
}

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('VMS API client', () => {
  it('writes a VMS credential to the VMS-scoped endpoint', async () => {
    respondWithJson();

    await api.credentials.save(vmsId, { username: 'operator', password: 'test-password' });

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}/credential`),
      expect.objectContaining({
        body: JSON.stringify({ username: 'operator', password: 'test-password' }),
        method: 'PUT',
      }),
    );
  });

  it('reads credential presence from the VMS-scoped status endpoint', async () => {
    respondWithJson();

    await api.credentials.status(vmsId);

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}/credential/status`),
      expect.anything(),
    );
    expect(vi.mocked(fetch).mock.calls[0][1]?.method).toBeUndefined();
  });

  it('creates a VMS target at the target collection endpoint', async () => {
    respondWithJson();

    await api.vms.create({
      code: 'north-nvr',
      organizationUnitId: '33333333-3333-4333-8333-333333333333',
      displayName: 'North NVR',
      vendor: 'DahuaCgi',
      endpoint: 'https://nvr.example.test',
      credentialReference: 'vms/north-nvr',
    });

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining('/api/v1/vms'),
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('reads one VMS target by id', async () => {
    respondWithJson();

    await api.vms.get(vmsId);

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}`),
      expect.anything(),
    );
    expect(vi.mocked(fetch).mock.calls[0][1]?.method).toBeUndefined();
  });

  it('lists discovered cameras from the selected VMS', async () => {
    respondWithJson();

    await api.vms.discoveredCameras(vmsId);

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}/cameras`),
      expect.anything(),
    );
  });

  it('starts an asynchronous connection test for the selected VMS', async () => {
    respondWithJson();

    await api.connectionTests.create(vmsId);

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}/test`),
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('polls the exact status URL returned for a connection test', async () => {
    respondWithJson();
    const statusUrl = `/api/v1/connection-test-results/${testId}?representation=safe`;

    await api.connectionTests.get(statusUrl);

    const [input, init] = vi.mocked(fetch).mock.calls[0];
    expect(String(input)).toBe(`${apiBaseUrl()}${statusUrl}`);
    expect(init?.method).toBeUndefined();
  });

  it('replaces a VMS target with a full configuration PUT', async () => {
    respondWithJson();

    await api.vms.replace(vmsId, {
      code: 'north-nvr',
      organizationUnitId: '33333333-3333-4333-8333-333333333333',
      displayName: 'North NVR',
      vendor: 'DahuaCgi',
      endpoint: 'https://nvr.example.test',
      credentialReference: 'vms/north-nvr',
    });

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}`),
      expect.objectContaining({ method: 'PUT' }),
    );
  });

  it('changes a target state at the state endpoint', async () => {
    respondWithJson();

    await api.vms.setState(vmsId, { state: 'Quarantined' });

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}/state`),
      expect.objectContaining({ method: 'POST', body: JSON.stringify({ state: 'Quarantined' }) }),
    );
  });

  it('removes a VMS target with a DELETE', async () => {
    respondWithJson();

    await api.vms.remove(vmsId);

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}`),
      expect.objectContaining({ method: 'DELETE' }),
    );
  });

  it('reads a target health history', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json([])));

    await api.vms.health(vmsId);

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}/health`),
      expect.anything(),
    );
  });

  it('reads a target capability matrix', async () => {
    respondWithJson();

    await api.vms.capabilities(vmsId);

    expect(fetch).toHaveBeenCalledWith(
      expect.stringContaining(`/api/v1/vms/${vmsId}/capabilities`),
      expect.anything(),
    );
  });

  it('applies the existing bearer authorization to VMS requests', async () => {
    respondWithJson();
    const session = sessionFixture('vms-operator');
    saveSession(session);

    await api.vms.list();

    const [, init] = vi.mocked(fetch).mock.calls[0];
    expect(new Headers(init?.headers).get('authorization')).toBe(`Bearer ${session.token}`);
  });
});
