import { zodResolver } from '@hookform/resolvers/zod';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';

import { isApiProblem } from '../../api/client';
import type { ConnectorTargetRequest, OrganizationResponse, OrganizationUnitResponse, SiteResponse } from '../../api/models';
import { Button } from '../../components/ui';
import type { SelectorState } from '../cameras/CameraForm';

const vendors = ['Onvif', 'HikvisionIsapi', 'DahuaCgi', 'MilestoneGateway', 'GenetecWebSdk', 'Simulator'] as const;
const runtimeClasses = ['Managed', 'Native'] as const;

type OptionalNumericField = 'rateLimitPerSecond' | 'rateLimitBurst' | 'inventoryPollSeconds' | 'statusPollSeconds' | 'eventPollSeconds' | 'maxConcurrentRequests' | 'expectedCameraCount';

interface VmsFormValues {
  code: string;
  organizationId: string;
  organizationUnitId: string;
  siteId: string;
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
  siteId: '',
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

const requiredText = (label: string) => z.string().trim().min(1, `${label} is required.`);

function optionalPositiveNumber(label: string, integer = false) {
  return z.string().trim().refine((value) => {
    if (!value) return true;
    const number = Number(value);
    return Number.isFinite(number) && number > 0 && (!integer || Number.isInteger(number));
  }, `${label} must be a positive${integer ? ' whole' : ''} number.`);
}

const vmsFormSchema = z.object({
  code: requiredText('VMS code'),
  organizationId: z.string(),
  organizationUnitId: requiredText('Organization unit'),
  siteId: z.string(),
  displayName: requiredText('Display name'),
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
  inventoryPollSeconds: optionalPositiveNumber('Inventory poll seconds', true),
  statusPollSeconds: optionalPositiveNumber('Status poll seconds', true),
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
  if (values.siteId) request.siteId = values.siteId;
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
  sites: SiteResponse[];
  selectorStates: {
    organizations: SelectorState;
    organizationUnits: SelectorState;
    sites: SelectorState;
  };
  onOrganizationChange(organizationId: string): void;
  onSubmit(values: ConnectorTargetRequest): Promise<void>;
}

export function VmsForm({ organizations, organizationUnits, sites, selectorStates, onOrganizationChange, onSubmit }: VmsFormProps) {
  const form = useForm<VmsFormValues>({ defaultValues: defaults, resolver: zodResolver(vmsFormSchema) });
  const [tuningOpen, setTuningOpen] = useState(false);
  const { register, formState: { errors, isSubmitting } } = form;
  const selectedOrganizationId = form.watch('organizationId');
  const organizationRegistration = register('organizationId');
  const validationProps = (name: keyof VmsFormValues, statusId?: string) => ({
    'aria-describedby': [errors[name] ? `${name}-error` : undefined, statusId].filter(Boolean).join(' ') || undefined,
    'aria-invalid': Boolean(errors[name]),
  });

  return <form className="vms-form" onSubmit={form.handleSubmit(async (values) => {
    form.clearErrors('root');
    try {
      await onSubmit(toConnectorTargetRequest(values));
    } catch (error) {
      form.setError('root', { message: isApiProblem(error) ? error.detail : 'Unable to register the VMS. Please try again.' });
    }
  })}>
    <fieldset>
      <legend>Target identity <span aria-hidden="true">* Required</span></legend>
      <label>VMS code<span aria-hidden="true"> *</span><input aria-required="true" {...register('code')} {...validationProps('code')} /></label><FieldError id="code-error" message={errors.code?.message} />
      <label>Display name<span aria-hidden="true"> *</span><input aria-required="true" {...register('displayName')} {...validationProps('displayName')} /></label><FieldError id="displayName-error" message={errors.displayName?.message} />
      <label>Organization<select aria-describedby={selectorStates.organizations.state === 'ready' ? undefined : 'vms-organizations-status'} disabled={selectorStates.organizations.state !== 'ready'} {...organizationRegistration} onChange={(event) => {
        organizationRegistration.onChange(event);
        form.setValue('organizationUnitId', '');
        form.clearErrors('organizationUnitId');
        onOrganizationChange(event.target.value);
      }}><option value="">Select an organization</option>{organizations.map((organization) => <option key={organization.id} value={organization.id}>{organization.name} ({organization.code})</option>)}</select></label>
      <SelectorStatus id="vms-organizations-status" label="Organizations" selector={selectorStates.organizations} emptyMessage="No organizations are available. Ask an administrator to create an organization before registering a VMS." />
      <label>Organization unit<span aria-hidden="true"> *</span><select aria-required="true" disabled={!selectedOrganizationId || selectorStates.organizationUnits.state !== 'ready'} {...register('organizationUnitId')} {...validationProps('organizationUnitId', selectorStates.organizationUnits.state === 'ready' ? undefined : 'vms-organization-units-status')}><option value="">Select an organization unit</option>{organizationUnits.map((unit) => <option key={unit.id} value={unit.id}>{unit.name} ({unit.code})</option>)}</select></label><FieldError id="organizationUnitId-error" message={errors.organizationUnitId?.message} />
      <SelectorStatus id="vms-organization-units-status" label="Organization units" selector={selectorStates.organizationUnits} idleMessage="Select an organization to load its organization units." emptyMessage="No organization units are available for this organization." />
      <label>Site<select disabled={selectorStates.sites.state !== 'ready'} {...register('siteId')} aria-describedby={selectorStates.sites.state === 'ready' ? undefined : 'vms-sites-status'}><option value="">No site selected</option>{sites.map((site) => <option key={site.id} value={site.id}>{site.name} ({site.code})</option>)}</select></label>
      <SelectorStatus id="vms-sites-status" label="Sites" selector={selectorStates.sites} emptyMessage="No sites are available. You can register this VMS without a site." />
    </fieldset>
    <fieldset>
      <legend>Connection configuration</legend>
      <label>Vendor<span aria-hidden="true"> *</span><select aria-required="true" {...register('vendor')} {...validationProps('vendor')}><option value="">Select a vendor adapter</option>{vendors.map((vendor) => <option key={vendor} value={vendor}>{vendor}</option>)}</select></label><FieldError id="vendor-error" message={errors.vendor?.message} />
      <label>Endpoint<span aria-hidden="true"> *</span><input aria-required="true" inputMode="url" placeholder="https://nvr.example.gov" {...register('endpoint')} {...validationProps('endpoint')} /></label><FieldError id="endpoint-error" message={errors.endpoint?.message} />
      <label>Credential reference<span aria-hidden="true"> *</span><input aria-required="true" autoComplete="off" {...register('credentialReference')} {...validationProps('credentialReference')} /></label><FieldError id="credentialReference-error" message={errors.credentialReference?.message} />
      <p className="field-help">This is a non-secret storage reference. Enter the device password or token only after the VMS is registered.</p>
      <label className="checkbox-label"><input type="checkbox" {...register('verifyTls')} /> Verify TLS certificate</label>
    </fieldset>
    <button aria-controls="vms-connection-tuning" aria-expanded={tuningOpen} className="button button--secondary" type="button" onClick={() => setTuningOpen((open) => !open)}>Connection tuning</button>
    {tuningOpen && <fieldset id="vms-connection-tuning">
      <legend>Optional connection tuning</legend>
      <label>Runtime class<select {...register('runtimeClass')}><option value="">Use server default</option>{runtimeClasses.map((runtimeClass) => <option key={runtimeClass} value={runtimeClass}>{runtimeClass}</option>)}</select></label>
      <label>Rate limit per second<input inputMode="decimal" type="number" min="0" step="any" {...register('rateLimitPerSecond')} {...validationProps('rateLimitPerSecond')} /></label><FieldError id="rateLimitPerSecond-error" message={errors.rateLimitPerSecond?.message} />
      <label>Rate limit burst<input inputMode="numeric" type="number" min="1" step="1" {...register('rateLimitBurst')} {...validationProps('rateLimitBurst')} /></label><FieldError id="rateLimitBurst-error" message={errors.rateLimitBurst?.message} />
      <label>Inventory poll seconds<input inputMode="numeric" type="number" min="1" step="1" {...register('inventoryPollSeconds')} {...validationProps('inventoryPollSeconds')} /></label><FieldError id="inventoryPollSeconds-error" message={errors.inventoryPollSeconds?.message} />
      <label>Status poll seconds<input inputMode="numeric" type="number" min="1" step="1" {...register('statusPollSeconds')} {...validationProps('statusPollSeconds')} /></label><FieldError id="statusPollSeconds-error" message={errors.statusPollSeconds?.message} />
      <label>Event poll seconds<input inputMode="numeric" type="number" min="1" step="1" {...register('eventPollSeconds')} {...validationProps('eventPollSeconds')} /></label><FieldError id="eventPollSeconds-error" message={errors.eventPollSeconds?.message} />
      <label>Maximum concurrent requests<input inputMode="numeric" type="number" min="1" step="1" {...register('maxConcurrentRequests')} {...validationProps('maxConcurrentRequests')} /></label><FieldError id="maxConcurrentRequests-error" message={errors.maxConcurrentRequests?.message} />
      <label>Expected camera count<input inputMode="numeric" type="number" min="0" step="1" {...register('expectedCameraCount')} {...validationProps('expectedCameraCount')} /></label><FieldError id="expectedCameraCount-error" message={errors.expectedCameraCount?.message} />
    </fieldset>}
    <FieldError id="vms-form-error" message={errors.root?.message} />
    <Button disabled={isSubmitting} type="submit">{isSubmitting ? 'Registering VMS…' : 'Register VMS'}</Button>
  </form>;
}
