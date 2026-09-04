import '@testing-library/jest-dom/vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';

import { LocationPicker } from './LocationPicker';

const emptyFeatures = { type: 'FeatureCollection' as const, features: [] };

function renderPicker(overrides: Partial<React.ComponentProps<typeof LocationPicker>> = {}) {
  const onLocationChange = vi.fn();
  const onAzimuthChange = vi.fn();
  render(<LocationPicker
    latitude={19.076}
    longitude={72.8777}
    azimuth={null}
    features={emptyFeatures}
    onLocationChange={onLocationChange}
    onAzimuthChange={onAzimuthChange}
    {...overrides}
  />);
  return { onLocationChange, onAzimuthChange };
}

it('emits rounded latitude and longitude when the pin moves', () => {
  const { onLocationChange } = renderPicker();

  fireEvent.click(screen.getByRole('button', { name: /set location to 19.076012345/i }));

  expect(onLocationChange).toHaveBeenCalledWith(19.0760123, 72.8777);
});

it('exposes an azimuth number alternative', () => {
  renderPicker();

  expect(screen.getByLabelText(/azimuth/i)).toHaveAttribute('min', '0');
  expect(screen.getByLabelText(/azimuth/i)).toHaveAttribute('max', '359.999');
});

it('routes valid numeric azimuth changes through the controlled callback', () => {
  const { onAzimuthChange } = renderPicker();

  fireEvent.change(screen.getByLabelText(/azimuth/i), { target: { value: '359.999' } });

  expect(onAzimuthChange).toHaveBeenCalledWith(359.999);
});

it('converts an eastward handle movement to a 90 degree azimuth', () => {
  const { onAzimuthChange } = renderPicker();

  fireEvent.click(screen.getByRole('button', { name: /point azimuth east/i }));

  expect(onAzimuthChange).toHaveBeenCalledWith(90);
});

it('does not emit an out-of-range numeric azimuth', () => {
  const { onAzimuthChange } = renderPicker();

  fireEvent.change(screen.getByLabelText(/azimuth/i), { target: { value: '360' } });

  expect(onAzimuthChange).not.toHaveBeenCalled();
});
