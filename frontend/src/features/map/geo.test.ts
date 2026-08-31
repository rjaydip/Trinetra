import { describe, expect, it } from 'vitest';

import { buildMapRequest } from './geo';

describe('buildMapRequest', () => {
  it('does not request a GIS feed wider than the API maximum', () => {
    expect(buildMapRequest([-76, 39, -70, 45], {})).toBeNull();
  });
});
