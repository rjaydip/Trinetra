import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { apiBaseUrl } from './client';
import { api } from './endpoints';

function fetchCall(path: string, init: RequestInit = {}): [string, RequestInit] {
  return [`${apiBaseUrl()}${path}`, expect.objectContaining(init) as RequestInit];
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({
    accessToken: 'new-access-token',
    accessExpiresIn: 900,
    refreshToken: 'new-refresh-token',
    refreshExpiresIn: 28800,
    mustChangePassword: false,
  })));
});

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('auth endpoints', () => {
  it('exchanges a refresh token at POST /api/v1/auth/refresh', async () => {
    const session = await api.auth.refresh('old-refresh-token');

    expect(fetch).toHaveBeenCalledWith(...fetchCall('/api/v1/auth/refresh', {
      method: 'POST', body: JSON.stringify({ refreshToken: 'old-refresh-token' }),
    }));
    expect(session.accessToken).toBe('new-access-token');
    expect(session.refreshToken).toBe('new-refresh-token');
  });

  it('ends the session at POST /api/v1/auth/logout', async () => {
    await api.auth.logout();

    expect(fetch).toHaveBeenCalledWith(...fetchCall('/api/v1/auth/logout', { method: 'POST' }));
  });
});
