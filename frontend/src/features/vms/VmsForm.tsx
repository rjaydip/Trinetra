import { zodResolver } from '@hookform/resolvers/zod';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { ConnectorTargetRequest, GeographicAreaResponse, OrganizationResponse, OrganizationUnitResponse } from '../../api/models';
import { Button, StatusBadge } from '../../components/ui';
import type { SelectorState } from '../cameras/CameraForm';
import { protocols } from '../cameras/cameraVocabulary';
import { TreeSelect } from '../cameras/TreeSelect';

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
  protocol: string;
  ipAddress: string;
  port: string;
  username: string;
  password: string;
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
  protocol: '',
  ipAddress: '',
  port: '',
  username: '',
  password: '',
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
  protocol: z.string().trim().min(1, 'Protocol is required.'),
  ipAddress: z.string().trim().min(1, 'IP address is required.'),
  port: z.string().trim().refine((value) => {
    const number = Number(value);
    return Number.isInteger(number) && number >= 1 && number <= 65535;
  }, 'Port must be between 1 and 65535.'),
  username: z.string(),
  password: z.string(),
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

function composeEndpoint(protocol: string, ipAddress: string, port: string) {
  return `${protocol.toLowerCase()}://${ipAddress.trim()}:${port.trim()}`;
}

export function toConnectorTargetRequest(values: VmsFormValues): ConnectorTargetRequest {
  const request: ConnectorTargetRequest = {
    code: values.code.trim(),
    organizationUnitId: values.organizationUnitId,
    displayName: values.displayName.trim(),
    vendor: values.vendor,
    endpoint: composeEndpoint(values.protocol, values.ipAddress, values.port),
    credentialReference: `vms/${values.code.trim().toLowerCase()}`,
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

export interface VmsCredentialInput {
  username?: string;
  password?: string;
}

function toCredentialInput(values: VmsFormValues): VmsCredentialInput {
  const credential: VmsCredentialInput = {};
  if (values.username.trim()) credential.username = values.username.trim();
  if (values.password) credential.password = values.password;
  return credential;
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

type ConnectionCheckState = { status: 'idle' } | { status: 'testing' } | { status: 'reachable' } | { status: 'unreachable'; detail: string } | { status: 'error'; detail: string };

/** Same generic, vendor-agnostic reachability probe used by the direct-camera registration form
 * (`api.cameras.testConnection` only checks protocol/ipAddress/port; it never sees a credential —
 * "Credentials are references, not fields"). Reused here rather than duplicated since the check
 * itself has nothing VMS-specific about it. */
function ConnectionCheck({ protocol, ipAddress, port }: { protocol: string; ipAddress: string; port: string }) {
  const [state, setState] = useState<ConnectionCheckState>({ status: 'idle' });
  const ready = Boolean(protocol && ipAddress.trim() && port.trim());

  async function runTest() {
    setState({ status: 'testing' });
    try {
      const result = await api.cameras.testConnection({ protocol, ipAddress: ipAddress.trim(), port: Number(port) });
      setState(result.reachable ? { status: 'reachable' } : { status: 'unreachable', detail: result.detail ?? 'The device did not respond.' });
    } catch (error) {
      setState({ status: 'error', detail: isApiProblem(error) ? error.detail : 'Unable to run the connectivity check. Please try again.' });
    }
  }

  return <div className="connection-check">
    <button className="button button--secondary" disabled={!ready || state.status === 'testing'} type="button" onClick={() => { void runTest(); }}>
      {state.status === 'testing' ? 'Testing connection…' : 'Test connection'}
    </button>
    <p className="field-help">Checks that the device answers at this address. It does not validate the credential.</p>
    {state.status === 'reachable' && <p role="status"><StatusBadge tone="success">Reachable</StatusBadge></p>}
    {state.status === 'unreachable' && <p role="status"><StatusBadge tone="warning">Not reachable</StatusBadge> {state.detail}</p>}
    {state.status === 'error' && <p className="form-error" role="alert">{state.detail}</p>}
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
  onSubmit(values: ConnectorTargetRequest, credential: VmsCredentialInput): Promise<void>;
}

export function VmsForm({ organizations, organizationUnits, geographicAreas, selectorStates, onOrganizationChange, onSubmit }: VmsFormProps) {
  const form = useForm<VmsFormValues>({ defaultValues: defaults, resolver: zodResolver(vmsFormSchema) });
  const [step, setStep] = useState<'connect' | 'details'>('connect');
  const [tuningOpen, setTuningOpen] = useState(false);
  const { register, formState: { errors, isSubmitting } } = form;
  const [protocolValue, ipAddressValue, portValue, organizationUnitIdValue, geographicAreaIdValue] = useWatch({
    control: form.control,
    name: ['protocol', 'ipAddress', 'port', 'organizationUnitId', 'geographicAreaId'],
  });
  const selectedOrganizationId = form.watch('organizationId');
  const organizationRegistration = register('organizationId');
  const validationProps = (name: keyof VmsFormValues, statusId?: string) => ({
    'aria-describedby': [errors[name] ? `${name}-error` : undefined, statusId].filter(Boolean).join(' ') || undefined,
    'aria-invalid': Boolean(errors[name]),
  });

  const connectStepFields: Array<keyof VmsFormValues> = ['code', 'displayName', 'protocol', 'ipAddress', 'port'];

  return <form className="vms-form" noValidate onSubmit={form.handleSubmit(async (values) => {
    form.clearErrors('root');
    try {
      await onSubmit(toConnectorTargetRequest(values), toCredentialInput(values));
    } catch (error) {
      form.setError('root', { message: isApiProblem(error) ? error.detail : 'Unable to register the VMS. Please try again.' });
    }
  }, (invalidFields) => {
    if (connectStepFields.some((field) => field in invalidFields)) setStep('connect');
  })}>
    <ol className="camera-form-steps" aria-label="Registration steps">
      <li aria-current={step === 'connect' ? 'step' : undefined}>1. Connect</li>
      <li aria-current={step === 'details' ? 'step' : undefined}>2. VMS details</li>
    </ol>
    {step === 'connect' && <fieldset>
      <legend>Connect to the device <span className="legend-note">* Required fields</span></legend>
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
        <span className="form-label-title">Protocol <span className="required-indicator" aria-hidden="true">*</span></span>
        <select aria-required="true" {...register('protocol')} {...validationProps('protocol')}>
          <option value="">Select a protocol</option>
          {protocols.map((protocol) => <option key={protocol} value={protocol}>{protocol}</option>)}
        </select>
        <FieldError id="protocol-error" message={errors.protocol?.message} />
      </label>
      <label>
        <span className="form-label-title">IP address <span className="required-indicator" aria-hidden="true">*</span></span>
        <input aria-required="true" {...register('ipAddress')} {...validationProps('ipAddress')} />
        <FieldError id="ipAddress-error" message={errors.ipAddress?.message} />
      </label>
      <label>
        <span className="form-label-title">Port <span className="required-indicator" aria-hidden="true">*</span></span>
        <input aria-required="true" type="number" step="1" {...register('port')} {...validationProps('port')} />
        <FieldError id="port-error" message={errors.port?.message} />
      </label>
      <label>
        <span className="form-label-title">Username</span>
        <input autoComplete="username" {...register('username')} />
      </label>
      <label>
        <span className="form-label-title">Password</span>
        <input autoComplete="new-password" type="password" {...register('password')} />
      </label>
      <p className="field-help">The credential is sealed to this VMS once registration completes — it is never stored as plain text or shown again.</p>
      <label className="checkbox-label">
        <input type="checkbox" {...register('verifyTls')} />
        <span>Verify TLS certificate</span>
      </label>
      <ConnectionCheck protocol={protocolValue} ipAddress={ipAddressValue} port={portValue} />
      <div className="form-actions">
        <Button type="button" onClick={() => setStep('details')}>Continue to VMS details</Button>
      </div>
    </fieldset>}
    {step === 'details' && <>
      <button className="button button--secondary" type="button" onClick={() => setStep('connect')}>Back to connection details</button>
      <fieldset>
        <legend>Vendor and placement <span className="legend-note">* Required fields</span></legend>
        <label>
          <span className="form-label-title">Vendor <span className="required-indicator" aria-hidden="true">*</span></span>
          <select aria-required="true" {...register('vendor')} {...validationProps('vendor')}>
            <option value="">Select a vendor adapter</option>
            {vendors.map((vendor) => <option key={vendor} value={vendor}>{vendor}</option>)}
          </select>
          <FieldError id="vendor-error" message={errors.vendor?.message} />
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
        <TreeSelect
          id="vms-organizationUnitId"
          label="Organization unit"
          items={organizationUnits}
          getParentId={(unit) => unit.parentUnitId}
          value={organizationUnitIdValue || undefined}
          onChange={(unitId) => form.setValue('organizationUnitId', unitId ?? '', { shouldDirty: true, shouldValidate: true })}
          disabled={!selectedOrganizationId || selectorStates.organizationUnits.state !== 'ready'}
          loading={selectorStates.organizationUnits.state === 'loading'}
          error={selectorStates.organizationUnits.state === 'error'}
          required
          invalid={Boolean(errors.organizationUnitId)}
          describedBy={[errors.organizationUnitId ? 'organizationUnitId-error' : undefined, selectorStates.organizationUnits.state === 'ready' ? undefined : 'vms-organization-units-status'].filter(Boolean).join(' ') || undefined}
          placeholder="Select an organization unit"
          emptyMessage="This organization has no units."
        />
        <FieldError id="organizationUnitId-error" message={errors.organizationUnitId?.message} />
        <SelectorStatus id="vms-organization-units-status" label="Organization units" selector={selectorStates.organizationUnits} idleMessage="Select an organization to load its organization units." emptyMessage="No organization units are available for this organization." />
        <TreeSelect
          id="vms-geographicAreaId"
          label="Geographic area"
          items={geographicAreas}
          getParentId={(area) => area.parentAreaId}
          value={geographicAreaIdValue || undefined}
          onChange={(areaId) => form.setValue('geographicAreaId', areaId ?? '', { shouldDirty: true, shouldValidate: true })}
          disabled={selectorStates.geographicAreas.state !== 'ready'}
          loading={selectorStates.geographicAreas.state === 'loading'}
          error={selectorStates.geographicAreas.state === 'error'}
          placeholder="No geographic area selected"
          emptyMessage="No geographic areas are available. You can register this VMS without one."
        />
        <SelectorStatus id="vms-geographic-areas-status" label="Geographic areas" selector={selectorStates.geographicAreas} emptyMessage="No geographic areas are available. You can register this VMS without one." />
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
    </>}
  </form>;
}
