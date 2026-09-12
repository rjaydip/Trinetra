import '@testing-library/jest-dom/vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';

import { LocationPicker } from './LocationPicker';

const maplibreSpies = vi.hoisted(() => ({
  addControl: vi.fn(),
  addLayer: vi.fn(),
  addSource: vi.fn(),
  mapOn: vi.fn(),
  mapRemove: vi.fn(),
  markerAddTo: vi.fn(),
  markerOn: vi.fn(),
  markerRemove: vi.fn(),
  markerSetLngLat: vi.fn(),
}));

vi.mock('maplibre-gl', () => {
  class FakeMap {
    addControl = maplibreSpies.addControl;
    addLayer = maplibreSpies.addLayer;
    addSource = maplibreSpies.addSource;
    easeTo = vi.fn();
    getSource = vi.fn();
    remove = maplibreSpies.mapRemove;
    on(event: string, listener: () => void) {
      maplibreSpies.mapOn(event, listener);
      if (event === 'load') listener();
      return this;
    }
  }

  class FakeMarker {
    constructor(private readonly options: { element: HTMLElement }) {}
    addTo() {
      maplibreSpies.markerAddTo();
      return this;
    }
    getElement() { return this.options.element; }
    getLngLat() { return { lat: 0, lng: 0 }; }
    on(event: string, listener: () => void) {
      maplibreSpies.markerOn(event, listener);
      return this;
    }
    remove() { maplibreSpies.markerRemove(); }
    setLngLat(value: [number, number]) {
      maplibreSpies.markerSetLngLat(value);
      return this;
    }
  }

  return { default: { Map: FakeMap, Marker: FakeMarker, NavigationControl: class {} } };
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.clearAllMocks();
});

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

it('wraps a dragged azimuth that rounds to 360 back to 0', () => {
  const { onAzimuthChange } = renderPicker();

  fireEvent.click(screen.getByRole('button', { name: /point azimuth just west of north/i }));

  expect(onAzimuthChange).toHaveBeenCalledWith(0);
});

it('initializes the marker from a controlled coordinate change made while MapLibre loads', async () => {
  vi.stubEnv('MODE', 'production');
  const onLocationChange = vi.fn();
  const onAzimuthChange = vi.fn();
  const { rerender } = render(<LocationPicker latitude={null} longitude={null} azimuth={null} features={emptyFeatures} onLocationChange={onLocationChange} onAzimuthChange={onAzimuthChange} />);

  rerender(<LocationPicker latitude={10.1234567} longitude={20.7654321} azimuth={null} features={emptyFeatures} onLocationChange={onLocationChange} onAzimuthChange={onAzimuthChange} />);

  await waitFor(() => expect(maplibreSpies.markerSetLngLat).toHaveBeenCalledWith([20.7654321, 10.1234567]));
  expect(maplibreSpies.markerAddTo).toHaveBeenCalledOnce();
});

it('does not emit an out-of-range numeric azimuth', () => {
  const { onAzimuthChange } = renderPicker();

  fireEvent.change(screen.getByLabelText(/azimuth/i), { target: { value: '360' } });

  expect(onAzimuthChange).not.toHaveBeenCalled();
});
