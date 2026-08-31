// @vitest-environment node

import { rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

const envFile = join(process.cwd(), '.env.test');

afterEach(async () => {
  await rm(envFile, { force: true });
  vi.resetModules();
});

describe('Vite configuration', () => {
  it('uses VITE_API_BASE_URL from the mode environment file for the API proxy', async () => {
    await writeFile(envFile, 'VITE_API_BASE_URL=https://registry.test.example\n');
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
