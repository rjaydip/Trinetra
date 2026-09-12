import { afterEach, describe, expect, it, vi } from 'vitest';

import { api } from './endpoints';
import { readSession, saveSession } from '../auth/session';
import { sessionFixture } from '../test/fixtures';

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('API client', () => {
  it('does not expire a replacement session when an old request returns 401 late', async () => {
    saveSession(sessionFixture('old'));
    let finish: (response: Response) => void = () => {};
    vi.stubGlobal('fetch', () => new Promise<Response>((resolve) => { finish = resolve; }));
    const request = api.cameras.list({});
    const replacement = sessionFixture('new');
    saveSession(replacement);
    finish(Response.json({ title: 'Expired' }, { status: 401 }));
    await expect(request).rejects.toMatchObject({ status: 401 });
    expect(readSession()?.token).toBe(replacement.token);
  });

  it('turns a Problem Details response into an ApiProblem', async () => {
    vi.stubGlobal('fetch', async () => new Response(JSON.stringify({
      title: 'Forbidden',
      detail: 'No camera.read permission.',
    }), {
      status: 403,
      headers: { 'content-type': 'application/problem+json' },
    }));

    await expect(api.cameras.list({})).rejects.toMatchObject({
      status: 403,
      title: 'Forbidden',
      detail: 'No camera.read permission.',
    });
  });

  it('keeps a non-Problem failure safe for inline display', async () => {
    vi.stubGlobal('fetch', async () => new Response('<html>gateway error</html>', {
      status: 502,
      headers: { 'content-type': 'text/html' },
    }));

    await expect(api.cameras.list({})).rejects.toMatchObject({
      status: 502,
      title: 'Request failed',
      detail: 'The service could not complete this request. Please try again.',
    });
  });
});
