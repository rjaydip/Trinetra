import { describe, expect, it } from 'vitest';

import { roundCoordinate } from './cameraVocabulary';

describe('roundCoordinate', () => {
  it.each([[19.076012345, 19.0760123], [-72.123456789, -72.1234568]])(
    'rounds %s to seven decimal places', (input, expected) => {
      expect(roundCoordinate(input)).toBe(expected);
    },
  );
});
