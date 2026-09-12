import { zodResolver } from '@hookform/resolvers/zod';
import { useEffect, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';

import { isApiProblem } from '../../api/client';
import type { CameraWriteRequest, GeoJsonFeatureCollection, GeographicAreaResponse, OrganizationResponse, OrganizationUnitResponse, VmsResponse } from '../../api/models';
import { Button } from '../../components/ui';
import {
  cameraTypes,
  connectivityStatuses,
  maintenanceStatuses,
  operationalStatuses,
  protocols,
  roundCoordinate,
} from './cameraVocabulary';
import { LocationPicker } from './LocationPicker';

const uuidSchema = z.guid();

type NumericField = 'altitude' | 'mountingHeight' | 'azimuth' | 'tilt' | 'horizontalFov' | 'verticalFov' | 'effectiveRange' | 'port';

export interface CameraFormValues {
  cameraCode: string;
  name: string;
  organizationId: string;
  organizationUnitId: string;
  geographicAreaId: string;
  cameraType: string;
  latitude: string;
  longitude: string;
  manufacturer: string;
  model: string;
  serialNumber: string;
  altitude: string;
  mountingHeight: string;
  azimuth: string;
  tilt: string;
  horizontalFov: string;
  verticalFov: string;
  effectiveRange: string;
  ipAddress: string;
  port: string;
  protocol: string;
  vmsId: string;
  streamReference: string;
  installationDate: string;
  operationalStatus: string;
  connectivityStatus: string;
  maintenanceStatus: string;
}

const defaults: CameraFormValues = {
  cameraCode: '', name: '', organizationId: '', organizationUnitId: '', geographicAreaId: '', cameraType: '', latitude: '', longitude: '',
  manufacturer: '', model: '', serialNumber: '', altitude: '', mountingHeight: '', azimuth: '', tilt: '',
  horizontalFov: '', verticalFov: '', effectiveRange: '', ipAddress: '', port: '', protocol: '', vmsId: '',
  streamReference: '', installationDate: '', operationalStatus: '', connectivityStatus: '', maintenanceStatus: '',
};

function numeric(label: string, minimum: number, maximum: number, required = false) {
  return z.string().trim().superRefine((value, context) => {
    if (!value) {
      if (required) context.addIssue({ code: 'custom', message: `${label} is required.` });
      return;
    }
    const number = Number(value);
    if (!Number.isFinite(number) || number < minimum || number > maximum) {
      context.addIssue({ code: 'custom', message: `${label} must be between ${minimum} and ${maximum}.` });
    }
  });
}

const requiredText = (label: string, maximum: number) => z.string().trim()
  .min(1, `${label} is required.`)
  .max(maximum, `${label} must be at most ${maximum} characters.`);

function choice(values: readonly string[], message: string, required = false) {
  return z.string().refine((value) => {
    if (!value) return !required;
    return values.includes(value);
  }, { message });
}

function optionalUuid(label: string) {
  return z.string().refine((value) => !value || uuidSchema.safeParse(value).success, { message: `${label} must be a valid UUID.` });
}

const cameraFormSchema = z.object({
  cameraCode: requiredText('Camera code', 100),
  name: requiredText('Name', 255),
  organizationId: z.string(),
  organizationUnitId: z.string().trim().min(1, 'Organization unit is required.'),
  geographicAreaId: z.string().trim().min(1, 'Geographic area is required.'),
  cameraType: choice(cameraTypes, 'Camera type is required.', true),
  latitude: numeric('Latitude', -90, 90, true),
  longitude: numeric('Longitude', -180, 180, true),
  manufacturer: z.string(), model: z.string(), serialNumber: z.string(),
  altitude: numeric('Altitude', -500, 9000),
  mountingHeight: numeric('Mounting height', 0, 200),
  azimuth: numeric('Azimuth', 0, 359.999),
  tilt: numeric('Tilt', -90, 90),
  horizontalFov: numeric('Horizontal field of view', 0.001, 360),
  verticalFov: numeric('Vertical field of view', 0.001, 180),
  effectiveRange: numeric('Effective range', 0.001, 5000),
  ipAddress: z.string(),
  port: numeric('Port', 1, 65535),
  protocol: choice(protocols, `Protocol must be one of: ${protocols.join(', ')}.`),
  vmsId: optionalUuid('VMS ID'),
  streamReference: z.string(),
  installationDate: z.string(),
  operationalStatus: choice(operationalStatuses, `Operational status must be one of: ${operationalStatuses.join(', ')}.`),
  connectivityStatus: choice(connectivityStatuses, `Connectivity status must be one of: ${connectivityStatuses.join(', ')}.`),
  maintenanceStatus: choice(maintenanceStatuses, `Maintenance status must be one of: ${maintenanceStatuses.join(', ')}.`),
}).superRefine((values, context) => {
  if (values.vmsId) return;

  const manualFields: Array<[keyof Pick<CameraFormValues, 'manufacturer' | 'ipAddress' | 'port' | 'protocol'>, string]> = [
    ['manufacturer', 'Manufacturer'],
    ['ipAddress', 'IP address'],
    ['port', 'Port'],
    ['protocol', 'Protocol'],
  ];
  manualFields.forEach(([field, label]) => {
    if (!values[field].trim()) {
      context.addIssue({ code: 'custom', path: [field], message: `${label} is required for manual registration.` });
    }
  });
});

function optionalText(value: string) {
  const trimmed = value.trim();
  return trimmed || undefined;
}

function optionalNumber(value: string) {
  const trimmed = value.trim();
  return trimmed ? Number(trimmed) : undefined;
}

export function toCameraWriteRequest(values: CameraFormValues): CameraWriteRequest {
  const request: CameraWriteRequest = {
    cameraCode: values.cameraCode.trim(), name: values.name.trim(), organizationUnitId: values.organizationUnitId,
    geographicAreaId: values.geographicAreaId, cameraType: values.cameraType,
    latitude: roundCoordinate(Number(values.latitude)), longitude: roundCoordinate(Number(values.longitude)),
  };
  const optionalStrings: Array<keyof Pick<CameraWriteRequest, 'manufacturer' | 'model' | 'serialNumber' | 'ipAddress' | 'protocol' | 'vmsId' | 'streamReference' | 'installationDate' | 'operationalStatus' | 'connectivityStatus' | 'maintenanceStatus'>> = [
    'manufacturer', 'model', 'serialNumber', 'ipAddress', 'protocol', 'vmsId', 'streamReference', 'installationDate',
    'operationalStatus', 'connectivityStatus', 'maintenanceStatus',
  ];
  optionalStrings.forEach((field) => {
    const value = optionalText(values[field]);
    if (value !== undefined) request[field] = value;
  });
  const optionalNumbers: NumericField[] = ['altitude', 'mountingHeight', 'azimuth', 'tilt', 'horizontalFov', 'verticalFov', 'effectiveRange', 'port'];
  optionalNumbers.forEach((field) => {
    const value = optionalNumber(values[field]);
    if (value !== undefined) request[field] = value;
  });
  return request;
}

interface CameraFormProps {
  organizations: OrganizationResponse[];
  organizationUnits: OrganizationUnitResponse[];
  geographicAreas: GeographicAreaResponse[];
  vms: VmsResponse[];
  selectorStates?: CameraSelectorStates;
  mapFeatures?: GeoJsonFeatureCollection;
  initialValues?: CameraFormValues;
  onOrganizationChange(organizationId: string): void;
  onCoordinatesChange?(latitude: number | null, longitude: number | null): void;
  onSubmit(values: CameraWriteRequest): Promise<void>;
}

export interface SelectorState {
  state: 'idle' | 'loading' | 'ready' | 'empty' | 'error';
  message?: string;
  retry?(): void;
}

export interface CameraSelectorStates {
  organizations: SelectorState;
  organizationUnits: SelectorState;
  geographicAreas: SelectorState;
  vms: SelectorState;
}

const readySelectorStates: CameraSelectorStates = {
  organizations: { state: 'ready' },
  organizationUnits: { state: 'ready' },
  geographicAreas: { state: 'ready' },
  vms: { state: 'ready' },
};

function FieldError({ id, message }: { id: string; message?: string }) {
  return message ? <p className="form-error" id={id} role="alert">{message}</p> : null;
}

function SelectorStatus({ id, label, selector, emptyMessage, idleMessage }: {
  id: string;
  label: string;
  selector: SelectorState;
  emptyMessage: string;
  idleMessage?: string;
}) {
  if (selector.state === 'ready') return null;

  const message = selector.state === 'idle' ? idleMessage
    : selector.state === 'loading' ? `Loading ${label.toLowerCase()}…`
      : selector.state === 'empty' ? emptyMessage
        : selector.message ?? `${label} could not be loaded. Please try again.`;

  return <div className="selector-status" id={id} role="status" aria-live="polite">
    <p>{message}</p>
    {selector.state === 'error' && selector.retry && <button className="button button--secondary" type="button" onClick={selector.retry}>Retry {label.toLowerCase()}</button>}
  </div>;
}

function coordinate(value: string, minimum: number, maximum: number) {
  const number = Number(value);
  return value.trim() && Number.isFinite(number) && number >= minimum && number <= maximum ? number : null;
}

function optionalNumericValue(value: string) {
  const number = Number(value);
  return value.trim() && Number.isFinite(number) ? number : null;
}

export function CameraForm({ organizations, organizationUnits, geographicAreas, vms, selectorStates = readySelectorStates, mapFeatures, initialValues, onOrganizationChange, onCoordinatesChange, onSubmit }: CameraFormProps) {
  const form = useForm<CameraFormValues>({ defaultValues: initialValues ?? defaults, resolver: zodResolver(cameraFormSchema) });
  const [detailsOpen, setDetailsOpen] = useState(false);
  const { register, formState: { errors, isSubmitting } } = form;
  const [latitudeValue, longitudeValue, azimuthValue] = useWatch({ control: form.control, name: ['latitude', 'longitude', 'azimuth'] });
  const latitude = coordinate(latitudeValue, -90, 90);
  const longitude = coordinate(longitudeValue, -180, 180);
  const azimuth = optionalNumericValue(azimuthValue);
  const selectedOrganizationId = form.watch('organizationId');
  const selectedVmsId = form.watch('vmsId');
  const organizationRegistration = register('organizationId');
  const validationProps = (name: keyof CameraFormValues, statusId?: string) => {
    const describedBy = [errors[name] ? `${name}-error` : undefined, statusId].filter(Boolean).join(' ') || undefined;
    return { 'aria-describedby': describedBy, 'aria-invalid': Boolean(errors[name]) };
  };
  const numericFields: Array<{ name: NumericField; label: string; step?: string }> = [
    { name: 'altitude', label: 'Altitude', step: 'any' }, { name: 'mountingHeight', label: 'Mounting height', step: 'any' },
    { name: 'tilt', label: 'Tilt', step: 'any' },
    { name: 'horizontalFov', label: 'Horizontal field of view', step: 'any' }, { name: 'verticalFov', label: 'Vertical field of view', step: 'any' },
    { name: 'effectiveRange', label: 'Effective range', step: 'any' }, { name: 'port', label: 'Port', step: '1' },
  ];

  useEffect(() => {
    onCoordinatesChange?.(latitude, longitude);
  }, [latitude, longitude, onCoordinatesChange]);

  return (
    <form className="camera-form" onSubmit={form.handleSubmit(async (values) => {
      form.clearErrors('root');
      try {
        await onSubmit(toCameraWriteRequest(values));
      } catch (error) {
        form.setError('root', { message: isApiProblem(error) ? error.detail : 'Unable to register the camera. Please try again.' });
      }
    })}>
      <fieldset><legend>Identity and location <span aria-hidden="true">* Required</span></legend>
        <label>Camera code<span aria-hidden="true"> *</span><input aria-required="true" {...register('cameraCode')} {...validationProps('cameraCode')} /></label><FieldError id="cameraCode-error" message={errors.cameraCode?.message} />
        <label>Name<span aria-hidden="true"> *</span><input aria-required="true" {...register('name')} {...validationProps('name')} /></label><FieldError id="name-error" message={errors.name?.message} />
        <label>Organization<select aria-describedby={selectorStates.organizations.state === 'ready' ? undefined : 'organizations-status'} disabled={selectorStates.organizations.state !== 'ready'} {...organizationRegistration} onChange={(event) => {
          organizationRegistration.onChange(event);
          form.setValue('organizationUnitId', '');
          form.clearErrors('organizationUnitId');
          onOrganizationChange(event.target.value);
        }}><option value="">Select an organization</option>{organizations.map((organization) => <option key={organization.id} value={organization.id}>{organization.name} ({organization.code})</option>)}</select></label>
        <SelectorStatus id="organizations-status" label="Organizations" selector={selectorStates.organizations} emptyMessage="No organizations are available. Ask an administrator to create an organization before registering a camera." />
        <label>Organization unit<span aria-hidden="true"> *</span><select aria-required="true" disabled={!selectedOrganizationId || selectorStates.organizationUnits.state !== 'ready'} {...register('organizationUnitId')} {...validationProps('organizationUnitId', selectorStates.organizationUnits.state === 'ready' ? undefined : 'organization-units-status')}><option value="">Select an organization unit</option>{organizationUnits.map((unit) => <option key={unit.id} value={unit.id}>{unit.name} ({unit.code})</option>)}</select></label><FieldError id="organizationUnitId-error" message={errors.organizationUnitId?.message} />
        <SelectorStatus id="organization-units-status" label="Organization units" selector={selectorStates.organizationUnits} idleMessage="Select an organization to load its organization units." emptyMessage="No organization units are available for this organization. Choose another organization or ask an administrator to create an organization unit." />
        <label>Geographic area<span aria-hidden="true"> *</span><select aria-required="true" disabled={selectorStates.geographicAreas.state !== 'ready'} {...register('geographicAreaId')} {...validationProps('geographicAreaId', selectorStates.geographicAreas.state === 'ready' ? undefined : 'geographic-areas-status')}><option value="">Select a geographic area</option>{geographicAreas.map((area) => <option key={area.id} value={area.id}>{area.name} ({area.code})</option>)}</select></label><FieldError id="geographicAreaId-error" message={errors.geographicAreaId?.message} />
        <SelectorStatus id="geographic-areas-status" label="Geographic areas" selector={selectorStates.geographicAreas} emptyMessage="No geographic areas are available. Ask an administrator to create one before registering a camera." />
        <label>Camera type<span aria-hidden="true"> *</span><select aria-required="true" {...register('cameraType')} {...validationProps('cameraType')}><option value="">Select a camera type</option>{cameraTypes.map((type) => <option key={type} value={type}>{type}</option>)}</select></label><FieldError id="cameraType-error" message={errors.cameraType?.message} />
        <label>Latitude<span aria-hidden="true"> *</span><input aria-required="true" inputMode="decimal" {...register('latitude')} {...validationProps('latitude')} /></label><FieldError id="latitude-error" message={errors.latitude?.message} />
        <label>Longitude<span aria-hidden="true"> *</span><input aria-required="true" inputMode="decimal" {...register('longitude')} {...validationProps('longitude')} /></label><FieldError id="longitude-error" message={errors.longitude?.message} />
      </fieldset>
      <fieldset><legend>Device and network</legend>
        <label>Manufacturer<span aria-hidden="true"> *</span><input aria-required={!selectedVmsId} {...register('manufacturer')} {...validationProps('manufacturer')} /></label><FieldError id="manufacturer-error" message={errors.manufacturer?.message} />
        <label>IP address<span aria-hidden="true"> *</span><input aria-required={!selectedVmsId} {...register('ipAddress')} {...validationProps('ipAddress')} /></label><FieldError id="ipAddress-error" message={errors.ipAddress?.message} />
        <label>Protocol<span aria-hidden="true"> *</span><select aria-required={!selectedVmsId} {...register('protocol')} {...validationProps('protocol')}><option value="">Not recorded</option>{protocols.map((protocol) => <option key={protocol} value={protocol}>{protocol}</option>)}</select></label><FieldError id="protocol-error" message={errors.protocol?.message} />
        <label>Port<span aria-hidden="true"> *</span><input aria-required={!selectedVmsId} type="number" step="1" {...register('port')} {...validationProps('port')} /></label><FieldError id="port-error" message={errors.port?.message} />
      </fieldset>
      <button aria-controls="camera-additional-details" aria-expanded={detailsOpen} className="button button--secondary" onClick={() => setDetailsOpen((open) => !open)} type="button">Additional details</button>
      {detailsOpen && <section id="camera-additional-details" aria-label="Additional details">
        <LocationPicker
          latitude={latitude}
          longitude={longitude}
          azimuth={azimuth}
          azimuthError={errors.azimuth?.message}
          features={mapFeatures}
          onLocationChange={(nextLatitude, nextLongitude) => {
            form.setValue('latitude', String(nextLatitude), { shouldDirty: true, shouldValidate: true });
            form.setValue('longitude', String(nextLongitude), { shouldDirty: true, shouldValidate: true });
          }}
          onAzimuthChange={(nextAzimuth) => form.setValue('azimuth', nextAzimuth === null ? '' : String(nextAzimuth), { shouldDirty: true, shouldValidate: true })}
        />
        <fieldset><legend>Optional device details</legend>
          <label>Model<input {...register('model')} {...validationProps('model')} /></label><label>Serial number<input {...register('serialNumber')} {...validationProps('serialNumber')} /></label>
          <label>VMS<select disabled={selectorStates.vms.state !== 'ready'} {...register('vmsId')} {...validationProps('vmsId', selectorStates.vms.state === 'ready' ? undefined : 'vms-status')}><option value="">Manual registration</option>{vms.map((vmsTarget) => <option key={vmsTarget.id} value={vmsTarget.id}>{vmsTarget.displayName} ({vmsTarget.code})</option>)}</select></label><FieldError id="vmsId-error" message={errors.vmsId?.message} />
          <SelectorStatus id="vms-status" label="VMS records" selector={selectorStates.vms} emptyMessage="No VMS records are available. Continue with manual registration, or ask an administrator to create a VMS record." />
          <label>Stream reference<input {...register('streamReference')} {...validationProps('streamReference')} /></label>
        </fieldset>
        <fieldset><legend>Position, optics, and status</legend>
          {numericFields.map(({ name, label, step }) => <div key={name}><label>{label}<input type="number" step={step} {...register(name)} {...validationProps(name)} /></label><FieldError id={`${name}-error`} message={errors[name]?.message} /></div>)}
          <label>Installation date<input type="date" {...register('installationDate')} {...validationProps('installationDate')} /></label>
          <label>Operational status<select {...register('operationalStatus')} {...validationProps('operationalStatus')}><option value="">Use server default</option>{operationalStatuses.map((status) => <option key={status} value={status}>{status}</option>)}</select></label><FieldError id="operationalStatus-error" message={errors.operationalStatus?.message} />
          <label>Connectivity status<select {...register('connectivityStatus')} {...validationProps('connectivityStatus')}><option value="">Use server default</option>{connectivityStatuses.map((status) => <option key={status} value={status}>{status}</option>)}</select></label><FieldError id="connectivityStatus-error" message={errors.connectivityStatus?.message} />
          <label>Maintenance status<select {...register('maintenanceStatus')} {...validationProps('maintenanceStatus')}><option value="">Use server default</option>{maintenanceStatuses.map((status) => <option key={status} value={status}>{status}</option>)}</select></label><FieldError id="maintenanceStatus-error" message={errors.maintenanceStatus?.message} />
        </fieldset>
      </section>}
      <FieldError id="camera-form-error" message={errors.root?.message} />
      <Button disabled={isSubmitting} type="submit">{isSubmitting ? 'Registering camera…' : 'Register camera'}</Button>
    </form>
  );
}
