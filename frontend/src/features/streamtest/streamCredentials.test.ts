import { describe, expect, it } from 'vitest';

import { extractStreamCredentials } from './streamCredentials';

describe('extractStreamCredentials', () => {
  it('extracts a username containing an "@" (an email address) without misreading where the userinfo ends', () => {
    const result = extractStreamCredentials('https://rajpara.jaydip1084%40gmail.com:TRKD-AUW5-D2F8@103.250.160.189:8554/whep/cam1');
    expect(result.username).toBe('rajpara.jaydip1084@gmail.com');
    expect(result.password).toBe('TRKD-AUW5-D2F8');
    expect(result.url).toBe('https://103.250.160.189:8554/whep/cam1');
  });

  it('leaves a URL with no embedded credentials unchanged, with no username/password', () => {
    const result = extractStreamCredentials('https://example.com/whep/cam1');
    expect(result.url).toBe('https://example.com/whep/cam1');
    expect(result.username).toBeUndefined();
    expect(result.password).toBeUndefined();
  });

  it('prefers explicit username/password fields over whatever is embedded in the URL', () => {
    const result = extractStreamCredentials('https://embedded-user:embedded-pass@example.com/whep', 'typed-user', 'typed-pass');
    expect(result.username).toBe('typed-user');
    expect(result.password).toBe('typed-pass');
    expect(result.url).toBe('https://example.com/whep');
  });

  it('falls back gracefully on a non-absolute or malformed URL instead of throwing', () => {
    const result = extractStreamCredentials('not a url', 'user', 'pass');
    expect(result).toEqual({ url: 'not a url', username: 'user', password: 'pass' });
  });
});
