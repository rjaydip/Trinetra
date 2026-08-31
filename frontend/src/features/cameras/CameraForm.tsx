import { zodResolver } from '@hookform/resolvers/zod';
import { useForm } from 'react-hook-form';
import { z } from 'zod';

import { isApiProblem } from '../../api/client';
import type { CameraWriteRequest, OrganizationUnitResponse, SiteResponse } from '../../api/models';
import { Button } from '../../components/ui';

const cameraTypes = ['FIXED', 'PTZ', 'DOME', 'BULLET', 'ANPR', 'THERMAL', 'MULTISENSOR', 'OTHER'] as const;
const protocols = ['RTSP', 'RTSPS', 'ONVIF', 'HTTP', 'HTTPS', 'RTMP', 'SRT', 'OTHER'] as const;
const operationalStatuses = ['ONLINE', 'OFFLINE', 'DEGRADED', 'UNKNOWN'] as const;
const connectivityStatuses = ['CONNECTED', 'DISCONNECTED', 'UNKNOWN'] as const;
const maintenanceStatuses = ['NORMAL', 'REQUIRED', 'UNDER_MAINTENANCE'] as const;

type NumericField = 'altitude' | 'mountingHeight' | 'azimuth' | 'tilt' | 'horizontalFov' | 'verticalFov' | 'effectiveRange' | 'port';

export interface CameraFormValues {
  cameraCode: string;
  name: string;
  organizationUnitId: string;
  siteId: string;
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
  cameraCode: '', name: '', organizationUnitId: '', siteId: '', cameraType: '', latitude: '', longitude: '',
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

const cameraFormSchema = z.object({
  cameraCode: requiredText('Camera code', 100),
  name: requiredText('Name', 255),
  organizationUnitId: z.string().trim().min(1, 'Organization unit is required.'),
  siteId: z.string().trim().min(1, 'Site is required.'),
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
  vmsId: z.string(),
  streamReference: z.string(),
  installationDate: z.string(),
  operationalStatus: choice(operationalStatuses, `Operational status must be one of: ${operationalStatuses.join(', ')}.`),
  connectivityStatus: choice(connectivityStatuses, `Connectivity status must be one of: ${connectivityStatuses.join(', ')}.`),
  maintenanceStatus: choice(maintenanceStatuses, `Maintenance status must be one of: ${maintenanceStatuses.join(', ')}.`),
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
    siteId: values.siteId, cameraType: values.cameraType, latitude: Number(values.latitude), longitude: Number(values.longitude),
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
  organizationUnits: OrganizationUnitResponse[];
  sites: SiteResponse[];
  initialValues?: CameraFormValues;
  onSubmit(values: CameraWriteRequest): Promise<void>;
}

function FieldError({ message }: { message?: string }) {
  return message ? <p className="form-error" role="alert">{message}</p> : null;
}

export function CameraForm({ organizationUnits, sites, initialValues, onSubmit }: CameraFormProps) {
  const form = useForm<CameraFormValues>({ defaultValues: initialValues ?? defaults, resolver: zodResolver(cameraFormSchema) });
  const { register, formState: { errors, isSubmitting } } = form;
  const numericFields: Array<{ name: NumericField; label: string; step?: string }> = [
    { name: 'altitude', label: 'Altitude', step: 'any' }, { name: 'mountingHeight', label: 'Mounting height', step: 'any' },
    { name: 'azimuth', label: 'Azimuth', step: 'any' }, { name: 'tilt', label: 'Tilt', step: 'any' },
    { name: 'horizontalFov', label: 'Horizontal field of view', step: 'any' }, { name: 'verticalFov', label: 'Vertical field of view', step: 'any' },
    { name: 'effectiveRange', label: 'Effective range', step: 'any' }, { name: 'port', label: 'Port', step: '1' },
  ];

  return (
    <form className="camera-form" onSubmit={form.handleSubmit(async (values) => {
      form.clearErrors('root');
      try {
        await onSubmit(toCameraWriteRequest(values));
      } catch (error) {
        form.setError('root', { message: isApiProblem(error) ? error.detail : 'Unable to register the camera. Please try again.' });
      }
    })}>
      <fieldset><legend>Identity and location</legend>
        <label>Camera code<input {...register('cameraCode')} aria-describedby={errors.cameraCode ? 'cameraCode-error' : undefined} /></label><FieldError message={errors.cameraCode?.message} />
        <label>Name<input {...register('name')} /></label><FieldError message={errors.name?.message} />
        <label>Organization unit<select {...register('organizationUnitId')}><option value="">Select an organization unit</option>{organizationUnits.map((unit) => <option key={unit.id} value={unit.id}>{unit.name} ({unit.code})</option>)}</select></label><FieldError message={errors.organizationUnitId?.message} />
        <label>Site<select {...register('siteId')}><option value="">Select a site</option>{sites.map((site) => <option key={site.id} value={site.id}>{site.name} ({site.code})</option>)}</select></label><FieldError message={errors.siteId?.message} />
        <label>Camera type<select {...register('cameraType')}><option value="">Select a camera type</option>{cameraTypes.map((type) => <option key={type} value={type}>{type}</option>)}</select></label><FieldError message={errors.cameraType?.message} />
        <label>Latitude<input inputMode="decimal" {...register('latitude')} /></label><FieldError message={errors.latitude?.message} />
        <label>Longitude<input inputMode="decimal" {...register('longitude')} /></label><FieldError message={errors.longitude?.message} />
      </fieldset>
      <fieldset><legend>Device and network</legend>
        <label>Manufacturer<input {...register('manufacturer')} /></label><label>Model<input {...register('model')} /></label><label>Serial number<input {...register('serialNumber')} /></label>
        <label>IP address<input {...register('ipAddress')} /></label>
        <label>Protocol<select {...register('protocol')}><option value="">Not recorded</option>{protocols.map((protocol) => <option key={protocol} value={protocol}>{protocol}</option>)}</select></label>
        <label>VMS ID<input {...register('vmsId')} /></label><label>Stream reference<input {...register('streamReference')} /></label>
      </fieldset>
      <fieldset><legend>Position, optics, and status</legend>
        {numericFields.map(({ name, label, step }) => <div key={name}><label>{label}<input type="number" step={step} {...register(name)} /></label><FieldError message={errors[name]?.message} /></div>)}
        <label>Installation date<input type="date" {...register('installationDate')} /></label>
        <label>Operational status<select {...register('operationalStatus')}><option value="">Use server default</option>{operationalStatuses.map((status) => <option key={status} value={status}>{status}</option>)}</select></label>
        <label>Connectivity status<select {...register('connectivityStatus')}><option value="">Use server default</option>{connectivityStatuses.map((status) => <option key={status} value={status}>{status}</option>)}</select></label>
        <label>Maintenance status<select {...register('maintenanceStatus')}><option value="">Use server default</option>{maintenanceStatuses.map((status) => <option key={status} value={status}>{status}</option>)}</select></label>
      </fieldset>
      <FieldError message={errors.root?.message} />
      <Button disabled={isSubmitting} type="submit">{isSubmitting ? 'Registering camera…' : 'Register camera'}</Button>
    </form>
  );
}
