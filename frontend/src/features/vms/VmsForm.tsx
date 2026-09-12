import { zodResolver } from '@hookform/resolvers/zod';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';

import { isApiProblem } from '../../api/client';
import type { ConnectorTargetRequest, GeographicAreaResponse, OrganizationResponse, OrganizationUnitResponse } from '../../api/models';
import { Button } from '../../components/ui';
import type { SelectorState } from '../cameras/CameraForm';

export const vendors = ['Onvif', 'HikvisionIsapi', 'DahuaCgi', 'MilestoneGateway', 'GenetecWebSdk', 'Simulator'] as const;
export const runtimeClasses = ['Managed', 'Native'] as const;

type OptionalNumericField = 'rateLimitPerSecond' | 'rateLimitBurst' | 'inventoryPollSeconds' | 'statusPollSeconds' | 'eventPollSeconds' | 'maxConcurrentRequests' | 'expectedCameraCount';

interface VmsFormValues {
  code: string;
  organizationId: string;
  organizationUnitId: string;
  geographicAreaId: string;
  displayName: string;
  vendor: '' | typeof vendors[number];
  endpoint: string;
  credentialReference: string;
  verifyTls: boolean;
  runtimeClass: '' | typeof runtimeClasses[number];
  rateLimitPerSecond: string;
  rateLimitBurst: string;
  inventoryPollSeconds: string;
  statusPollSeconds: string;
  eventPollSeconds: string;
  maxConcurrentRequests: string;
  expectedCameraCount: string;
}

const defaults: VmsFormValues = {
  code: '',
  organizationId: '',
  organizationUnitId: '',
  geographicAreaId: '',
  displayName: '',
  vendor: '',
  endpoint: '',
  credentialReference: '',
  verifyTls: true,
  runtimeClass: '',
  rateLimitPerSecond: '',
  rateLimitBurst: '',
  inventoryPollSeconds: '',
  statusPollSeconds: '',
  eventPollSeconds: '',
  maxConcurrentRequests: '',
  expectedCameraCount: '',
};

function requiredText(label: string, maximum?: number) {
  const schema = z.string().trim().min(1, `${label} is required.`);
  return maximum === undefined ? schema : schema.max(maximum, `${label} must be at most ${maximum} characters.`);
}

function optionalPositiveNumber(label: string, integer = false) {
  return z.string().trim().refine((value) => {
    if (!value) return true;
    const number = Number(value);
    return Number.isFinite(number) && number > 0 && (!integer || Number.isInteger(number));
  }, `${label} must be a positive${integer ? ' whole' : ''} number.`);
}

function optionalWholeNumberAtLeast(label: string, minimum: number) {
  return z.string().trim().refine((value) => {
    if (!value) return true;
    const number = Number(value);
    return Number.isInteger(number) && number >= minimum;
  }, `${label} must be ${minimum} or more.`);
}

const vmsFormSchema = z.object({
  code: requiredText('VMS code', 100),
  organizationId: z.string(),
  organizationUnitId: requiredText('Organization unit'),
  geographicAreaId: z.string(),
  displayName: requiredText('Display name', 255),
  vendor: z.union([z.literal(''), z.enum(vendors)]).refine((value) => Boolean(value), 'Vendor is required.'),
  endpoint: requiredText('Endpoint').refine((value) => {
    try {
      new URL(value.match(/^https?:\/\//i) ? value : `http://${value}`);
      return true;
    } catch {
      return false;
    }
  }, 'Endpoint must be a usable address.'),
  credentialReference: requiredText('Credential reference'),
  verifyTls: z.boolean(),
  runtimeClass: z.union([z.literal(''), z.enum(runtimeClasses)]),
  rateLimitPerSecond: optionalPositiveNumber('Rate limit per second'),
  rateLimitBurst: optionalPositiveNumber('Rate limit burst', true),
  inventoryPollSeconds: optionalWholeNumberAtLeast('Inventory poll seconds', 30),
  statusPollSeconds: optionalWholeNumberAtLeast('Status poll seconds', 5),
  eventPollSeconds: optionalPositiveNumber('Event poll seconds', true),
  maxConcurrentRequests: optionalPositiveNumber('Maximum concurrent requests', true),
  expectedCameraCount: z.string().trim().refine((value) => !value || (Number.isInteger(Number(value)) && Number(value) >= 0), 'Expected camera count must be zero or a positive whole number.'),
});

function optionalNumber(value: string): number | undefined {
  const trimmed = value.trim();
  return trimmed ? Number(trimmed) : undefined;
}

export function toConnectorTargetRequest(values: VmsFormValues): ConnectorTargetRequest {
  const request: ConnectorTargetRequest = {
    code: values.code.trim(),
    organizationUnitId: values.organizationUnitId,
    displayName: values.displayName.trim(),
    vendor: values.vendor,
    endpoint: values.endpoint.trim(),
    credentialReference: values.credentialReference.trim(),
    verifyTls: values.verifyTls,
  };
  if (values.geographicAreaId) request.geographicAreaId = values.geographicAreaId;
  if (values.runtimeClass) request.runtimeClass = values.runtimeClass;
  const numericFields: OptionalNumericField[] = [
    'rateLimitPerSecond', 'rateLimitBurst', 'inventoryPollSeconds', 'statusPollSeconds',
    'eventPollSeconds', 'maxConcurrentRequests', 'expectedCameraCount',
  ];
  numericFields.forEach((field) => {
    const value = optionalNumber(values[field]);
    if (value !== undefined) request[field] = value;
  });
  return request;
}

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

interface VmsFormProps {
  organizations: OrganizationResponse[];
  organizationUnits: OrganizationUnitResponse[];
  geographicAreas: GeographicAreaResponse[];
  selectorStates: {
    organizations: SelectorState;
    organizationUnits: SelectorState;
    geographicAreas: SelectorState;
  };
  onOrganizationChange(organizationId: string): void;
  onSubmit(values: ConnectorTargetRequest): Promise<void>;
}

export function VmsForm({ organizations, organizationUnits, geographicAreas, selectorStates, onOrganizationChange, onSubmit }: VmsFormProps) {
  const form = useForm<VmsFormValues>({ defaultValues: defaults, resolver: zodResolver(vmsFormSchema) });
  const [tuningOpen, setTuningOpen] = useState(false);
  const { register, formState: { errors, isSubmitting } } = form;
  const selectedOrganizationId = form.watch('organizationId');
  const organizationRegistration = register('organizationId');
  const validationProps = (name: keyof VmsFormValues, statusId?: string) => ({
    'aria-describedby': [errors[name] ? `${name}-error` : undefined, statusId].filter(Boolean).join(' ') || undefined,
    'aria-invalid': Boolean(errors[name]),
  });

  return <form className="vms-form" noValidate onSubmit={form.handleSubmit(async (values) => {
    form.clearErrors('root');
    try {
      await onSubmit(toConnectorTargetRequest(values));
    } catch (error) {
      form.setError('root', { message: isApiProblem(error) ? error.detail : 'Unable to register the VMS. Please try again.' });
    }
  })}>
    <fieldset>
      <legend>Target identity <span className="legend-note">* Required fields</span></legend>
      <label>
        <span className="form-label-title">VMS code <span className="required-indicator" aria-hidden="true">*</span></span>
        <input aria-required="true" maxLength={100} {...register('code')} {...validationProps('code')} />
        <FieldError id="code-error" message={errors.code?.message} />
      </label>
      <label>
        <span className="form-label-title">Display name <span className="required-indicator" aria-hidden="true">*</span></span>
        <input aria-required="true" maxLength={255} {...register('displayName')} {...validationProps('displayName')} />
        <FieldError id="displayName-error" message={errors.displayName?.message} />
      </label>
      <label>
        <span className="form-label-title">Organization</span>
        <select aria-describedby={selectorStates.organizations.state === 'ready' ? undefined : 'vms-organizations-status'} disabled={selectorStates.organizations.state !== 'ready'} {...organizationRegistration} onChange={(event) => {
          organizationRegistration.onChange(event);
          form.setValue('organizationUnitId', '');
          form.clearErrors('organizationUnitId');
          onOrganizationChange(event.target.value);
        }}>
          <option value="">Select an organization</option>
          {organizations.map((organization) => <option key={organization.id} value={organization.id}>{organization.name} ({organization.code})</option>)}
        </select>
      </label>
      <SelectorStatus id="vms-organizations-status" label="Organizations" selector={selectorStates.organizations} emptyMessage="No organizations are available. Ask an administrator to create an organization before registering a VMS." />
      <label>
        <span className="form-label-title">Organization unit <span className="required-indicator" aria-hidden="true">*</span></span>
        <select aria-required="true" disabled={!selectedOrganizationId || selectorStates.organizationUnits.state !== 'ready'} {...register('organizationUnitId')} {...validationProps('organizationUnitId', selectorStates.organizationUnits.state === 'ready' ? undefined : 'vms-organization-units-status')}>
          <option value="">Select an organization unit</option>
          {organizationUnits.map((unit) => <option key={unit.id} value={unit.id}>{unit.name} ({unit.code})</option>)}
        </select>
        <FieldError id="organizationUnitId-error" message={errors.organizationUnitId?.message} />
      </label>
      <SelectorStatus id="vms-organization-units-status" label="Organization units" selector={selectorStates.organizationUnits} idleMessage="Select an organization to load its organization units." emptyMessage="No organization units are available for this organization." />
      <label>
        <span className="form-label-title">Geographic area</span>
        <select disabled={selectorStates.geographicAreas.state !== 'ready'} {...register('geographicAreaId')} aria-describedby={selectorStates.geographicAreas.state === 'ready' ? undefined : 'vms-geographic-areas-status'}>
          <option value="">No geographic area selected</option>
          {geographicAreas.map((area) => <option key={area.id} value={area.id}>{area.name} ({area.code})</option>)}
        </select>
      </label>
      <SelectorStatus id="vms-geographic-areas-status" label="Geographic areas" selector={selectorStates.geographicAreas} emptyMessage="No geographic areas are available. You can register this VMS without one." />
    </fieldset>
    <fieldset>
      <legend>Connection configuration <span className="legend-note">* Required fields</span></legend>
      <label>
        <span className="form-label-title">Vendor <span className="required-indicator" aria-hidden="true">*</span></span>
        <select aria-required="true" {...register('vendor')} {...validationProps('vendor')}>
          <option value="">Select a vendor adapter</option>
          {vendors.map((vendor) => <option key={vendor} value={vendor}>{vendor}</option>)}
        </select>
        <FieldError id="vendor-error" message={errors.vendor?.message} />
      </label>
      <label>
        <span className="form-label-title">Endpoint <span className="required-indicator" aria-hidden="true">*</span></span>
        <input aria-required="true" inputMode="url" placeholder="https://nvr.example.gov" {...register('endpoint')} {...validationProps('endpoint')} />
        <FieldError id="endpoint-error" message={errors.endpoint?.message} />
      </label>
      <label>
        <span className="form-label-title">Credential reference <span className="required-indicator" aria-hidden="true">*</span></span>
        <input aria-required="true" autoComplete="off" {...register('credentialReference')} {...validationProps('credentialReference')} />
        <FieldError id="credentialReference-error" message={errors.credentialReference?.message} />
      </label>
      <p className="field-help">This is a non-secret storage reference. Enter the device password or token only after the VMS is registered.</p>
      <label className="checkbox-label">
        <input type="checkbox" {...register('verifyTls')} />
        <span>Verify TLS certificate</span>
      </label>
    </fieldset>
    <button aria-controls="vms-connection-tuning" aria-expanded={tuningOpen} className="button button--secondary" type="button" onClick={() => setTuningOpen((open) => !open)}>Connection tuning</button>
    {tuningOpen && <fieldset id="vms-connection-tuning">
      <legend>Optional connection tuning</legend>
      <label>
        <span className="form-label-title">Runtime class</span>
        <select {...register('runtimeClass')}>
          <option value="">Use server default</option>
          {runtimeClasses.map((runtimeClass) => <option key={runtimeClass} value={runtimeClass}>{runtimeClass}</option>)}
        </select>
      </label>
      <label>
        <span className="form-label-title">Rate limit per second</span>
        <input inputMode="decimal" type="number" min="0" step="any" {...register('rateLimitPerSecond')} {...validationProps('rateLimitPerSecond')} />
        <FieldError id="rateLimitPerSecond-error" message={errors.rateLimitPerSecond?.message} />
      </label>
      <label>
        <span className="form-label-title">Rate limit burst</span>
        <input inputMode="numeric" type="number" min="1" step="1" {...register('rateLimitBurst')} {...validationProps('rateLimitBurst')} />
        <FieldError id="rateLimitBurst-error" message={errors.rateLimitBurst?.message} />
      </label>
      <label>
        <span className="form-label-title">Inventory poll seconds</span>
        <input inputMode="numeric" type="number" min="30" step="1" {...register('inventoryPollSeconds')} {...validationProps('inventoryPollSeconds')} />
        <FieldError id="inventoryPollSeconds-error" message={errors.inventoryPollSeconds?.message} />
      </label>
      <label>
        <span className="form-label-title">Status poll seconds</span>
        <input inputMode="numeric" type="number" min="5" step="1" {...register('statusPollSeconds')} {...validationProps('statusPollSeconds')} />
        <FieldError id="statusPollSeconds-error" message={errors.statusPollSeconds?.message} />
      </label>
      <label>
        <span className="form-label-title">Event poll seconds</span>
        <input inputMode="numeric" type="number" min="1" step="1" {...register('eventPollSeconds')} {...validationProps('eventPollSeconds')} />
        <FieldError id="eventPollSeconds-error" message={errors.eventPollSeconds?.message} />
      </label>
      <label>
        <span className="form-label-title">Maximum concurrent requests</span>
        <input inputMode="numeric" type="number" min="1" step="1" {...register('maxConcurrentRequests')} {...validationProps('maxConcurrentRequests')} />
        <FieldError id="maxConcurrentRequests-error" message={errors.maxConcurrentRequests?.message} />
      </label>
      <label>
        <span className="form-label-title">Expected camera count</span>
        <input inputMode="numeric" type="number" min="0" step="1" {...register('expectedCameraCount')} {...validationProps('expectedCameraCount')} />
        <FieldError id="expectedCameraCount-error" message={errors.expectedCameraCount?.message} />
      </label>
    </fieldset>}
    <FieldError id="vms-form-error" message={errors.root?.message} />
    <Button disabled={isSubmitting} type="submit">{isSubmitting ? 'Registering VMS…' : 'Register VMS'}</Button>
  </form>;
}
