// @vitest-environment node

import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('Vite configuration test isolation', () => {
  it('does not target the project .env.test file', async () => {
    const source = await readFile(join(process.cwd(), 'src/viteConfiguration.test.ts'), 'utf8');

    expect(source).not.toContain("join(process.cwd(), '.env.test')");
  });
});
