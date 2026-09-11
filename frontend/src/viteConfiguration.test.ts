// @vitest-environment node

import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

let originalWorkingDirectory: string;
let testDirectory: string;
let originalApiBaseUrl: string | undefined;

beforeEach(async () => {
  originalWorkingDirectory = process.cwd();
  originalApiBaseUrl = process.env.VITE_API_BASE_URL;
  delete process.env.VITE_API_BASE_URL;
  testDirectory = await mkdtemp(join(tmpdir(), 'trinetra-vite-config-'));
  process.chdir(testDirectory);
});

afterEach(async () => {
  process.chdir(originalWorkingDirectory);
  if (originalApiBaseUrl === undefined) {
    delete process.env.VITE_API_BASE_URL;
  } else {
    process.env.VITE_API_BASE_URL = originalApiBaseUrl;
  }
  await rm(testDirectory, { force: true, recursive: true });
  vi.resetModules();
});

describe('Vite configuration', () => {
  it('uses VITE_API_BASE_URL from the mode environment file for the API proxy', async () => {
    await writeFile(join(testDirectory, '.env.test'), 'VITE_API_BASE_URL=https://registry.test.example\n');
    vi.resetModules();

    const viteConfig = (await import('../vite.config')).default;
    const config = await (typeof viteConfig === 'function'
      ? viteConfig({ command: 'serve', mode: 'test', isSsrBuild: false, isPreview: false })
      : viteConfig) as {
      server: { proxy: Record<string, { target: string }> };
    };

    expect(config.server.proxy['/api'].target).toBe('https://registry.test.example');
  });
});
