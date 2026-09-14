import { zodResolver } from '@hookform/resolvers/zod';
import { useEffect, useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { CameraPatchRequest, CameraWriteRequest, GeoJsonFeatureCollection, GeographicAreaResponse, OrganizationResponse, OrganizationUnitResponse, VmsResponse } from '../../api/models';
import { Button, StatusBadge } from '../../components/ui';
import { CredentialLibraryPicker } from '../credentials/CredentialLibraryPicker';
import {
  cameraTypes,
  connectivityStatuses,
  maintenanceStatuses,
  operationalStatuses,
  protocols,
  roundCoordinate,
  streamPreferences,
} from './cameraVocabulary';
import { LocationPicker } from './LocationPicker';
import { TreeSelect } from './TreeSelect';

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
  username: string;
  password: string;
  recordEvents: boolean;
  vmsId: string;
  streamReference: string;
  streamPreference: string;
  nativeHlsUrl: string;
  nativeWebrtcUrl: string;
  installationDate: string;
  operationalStatus: string;
  connectivityStatus: string;
  maintenanceStatus: string;
}

const defaults: CameraFormValues = {
  cameraCode: '', name: '', organizationId: '', organizationUnitId: '', geographicAreaId: '', cameraType: '', latitude: '', longitude: '',
  manufacturer: '', model: '', serialNumber: '', altitude: '', mountingHeight: '', azimuth: '', tilt: '',
  horizontalFov: '', verticalFov: '', effectiveRange: '', ipAddress: '', port: '', protocol: '', username: '', password: '', recordEvents: true, vmsId: '',
  streamReference: '', streamPreference: 'RTSP', nativeHlsUrl: '', nativeWebrtcUrl: '',
  installationDate: '', operationalStatus: '', connectivityStatus: '', maintenanceStatus: '',
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
  username: z.string(),
  password: z.string(),
  recordEvents: z.boolean(),
  vmsId: optionalUuid('VMS ID'),
  streamReference: z.string(),
  streamPreference: choice(streamPreferences, `Stream preference must be one of: ${streamPreferences.join(', ')}.`),
  nativeHlsUrl: z.string(),
  nativeWebrtcUrl: z.string(),
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
}).superRefine((values, context) => {
  if (values.streamPreference === 'HLS' && !values.nativeHlsUrl.trim()) {
    context.addIssue({ code: 'custom', path: ['nativeHlsUrl'], message: 'Native HLS URL is required when stream preference is HLS.' });
  }
  if (values.streamPreference === 'WEBRTC' && !values.nativeWebrtcUrl.trim()) {
    context.addIssue({ code: 'custom', path: ['nativeWebrtcUrl'], message: 'Native WebRTC URL is required when stream preference is WebRTC.' });
  }
});

function optionalText(value: string) {
  const trimmed = value.trim();
  return trimmed || undefined;
}

function optionalNumber(value: string) {
  const trimmed = value.trim();
  return trimmed ? Number(trimmed) : undefined;
}

/** `savedCredentialReference` points the camera at a shared saved-credential library entry
 * (v1.23) instead of its own sealed username/password — a picker choice, not a form field, so it
 * isn't part of `CameraFormValues`. */
export function toCameraWriteRequest(values: CameraFormValues, savedCredentialReference?: string): CameraWriteRequest {
  const request: CameraWriteRequest = {
    cameraCode: values.cameraCode.trim(), name: values.name.trim(), organizationUnitId: values.organizationUnitId,
    geographicAreaId: values.geographicAreaId, cameraType: values.cameraType,
    latitude: roundCoordinate(Number(values.latitude)), longitude: roundCoordinate(Number(values.longitude)),
    recordEvents: values.recordEvents,
    streamPreference: values.streamPreference || 'RTSP',
  };
  if (savedCredentialReference) request.credentialReference = savedCredentialReference;
  const optionalStrings: Array<keyof Pick<CameraWriteRequest, 'manufacturer' | 'model' | 'serialNumber' | 'ipAddress' | 'protocol' | 'vmsId' | 'streamReference' | 'nativeHlsUrl' | 'nativeWebrtcUrl' | 'installationDate' | 'operationalStatus' | 'connectivityStatus' | 'maintenanceStatus'>> = [
    'manufacturer', 'model', 'serialNumber', 'ipAddress', 'protocol', 'vmsId', 'streamReference', 'nativeHlsUrl', 'nativeWebrtcUrl', 'installationDate',
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

export interface CameraCredentialInput {
  username?: string;
  password?: string;
}

function toCameraCredentialInput(values: CameraFormValues): CameraCredentialInput {
  const credential: CameraCredentialInput = {};
  if (values.username.trim()) credential.username = values.username.trim();
  if (values.password) credential.password = values.password;
  return credential;
}

/** Fields the final ("Camera details") step saves via `PATCH /api/v1/cameras/{id}` — the camera
 * itself was already created (and its network/credential fields already saved) during the
 * "Connect"-step credential test, so `cameraCode` (immutable after create) is dropped here. */
export function toCameraPatchRequest(values: CameraFormValues): CameraPatchRequest {
  const { cameraCode: _cameraCode, ...patch } = toCameraWriteRequest(values);
  return patch;
}

/** The fields `POST /api/v1/cameras` requires (Model 1 API plan §2.7) plus network fields the
 * credential test needs — the minimal set that must be valid before "Test connection" can run. */
const requiredForTestFields = [
  'cameraCode', 'name', 'organizationUnitId', 'geographicAreaId', 'cameraType', 'latitude', 'longitude',
  'protocol', 'ipAddress', 'port',
] as const satisfies readonly (keyof CameraFormValues)[];

const CAMERA_DRAFT_STORAGE_KEY = 'trinetra.cameraForm.draft';

/** The credential is excluded from the persisted draft — it's sealed server-side once the
 * connection test runs, and there's no reason to also hold it in plaintext in localStorage. */
type CameraDraftValues = Omit<CameraFormValues, 'password'>;

function readCameraDraft(): CameraDraftValues | null {
  try {
    const raw = window.localStorage.getItem(CAMERA_DRAFT_STORAGE_KEY);
    return raw ? (JSON.parse(raw) as CameraDraftValues) : null;
  } catch {
    return null;
  }
}

function clearCameraDraft() {
  try {
    window.localStorage.removeItem(CAMERA_DRAFT_STORAGE_KEY);
  } catch {
    // localStorage unavailable (private browsing, quota) — nothing to clear.
  }
}

interface CameraFormProps {
  organizations: OrganizationResponse[];
  organizationUnits: OrganizationUnitResponse[];
  geographicAreas: GeographicAreaResponse[];
  vms: VmsResponse[];
  selectorStates?: CameraSelectorStates;
  mapFeatures?: GeoJsonFeatureCollection;
  initialValues?: CameraFormValues;
  /** The camera being edited. Required alongside `initialValues` — the create flow discovers its
   * camera id from the "Test connection" step instead, so this is how the edit flow (which skips
   * that step) seeds the id the submit handler needs. */
  cameraId?: string;
  /** The camera's existing `credentialReference`, if any — seeds the saved-credential picker so
   * an operator editing a camera that already shares a library entry sees which one, instead of
   * the picker always starting on "Enter manually below" regardless of what's actually set.
   * `CredentialLibraryPicker` itself reconciles this against the loaded library list: a reference
   * that isn't a shared library entry (a camera with its own self-sealed credential) falls back
   * to manual once the list is known, rather than looking selected with nothing to show for it. */
  initialCredentialReference?: string;
  onOrganizationChange(organizationId: string): void;
  onCoordinatesChange?(latitude: number | null, longitude: number | null): void;
  onSubmit(cameraId: string, values: CameraPatchRequest): Promise<void>;
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

type CredentialTestState =
  | { status: 'idle' }
  | { status: 'testing' }
  | { status: 'authenticated' }
  | { status: 'not_verifiable'; detail: string }
  | { status: 'credential_rejected'; detail: string }
  | { status: 'unreachable'; detail: string }
  | { status: 'error'; detail: string };

export function CameraForm({ organizations, organizationUnits, geographicAreas, vms, selectorStates = readySelectorStates, mapFeatures, initialValues, initialCredentialReference, cameraId: initialCameraId, onOrganizationChange, onCoordinatesChange, onSubmit }: CameraFormProps) {
  // Drafts only apply to the create flow — an edit form always starts from `initialValues`
  // (the real camera row), and restoring a stray localStorage draft over it would silently
  // clobber the operator's view of the current record.
  const isCreateFlow = !initialValues;
  // A caller can pass `initialValues` purely to prefill valid data while still exercising the
  // full create-flow wizard (as several `CameraForm.test.tsx` cases do, with no camera created
  // yet) — the wizard-skip behavior below must key off whether a *real, already-created* camera
  // id was seeded, not off `initialValues` alone.
  const isEditFlow = Boolean(initialCameraId);
  const [storedDraft] = useState<CameraDraftValues | null>(() => (isCreateFlow ? readCameraDraft() : null));
  const [draftBannerVisible, setDraftBannerVisible] = useState(Boolean(storedDraft));
  const form = useForm<CameraFormValues>({ defaultValues: initialValues ?? defaults, resolver: zodResolver(cameraFormSchema) });
  // The create flow's "Connect" step exists to test a device before the camera is created; an
  // edit is of an already-created, already-tested camera, so it opens straight on "Camera
  // details" (the operator can still step back to "Connect" to change and re-test network fields).
  const [step, setStep] = useState<'connect' | 'details'>(isEditFlow ? 'details' : 'connect');
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [cameraId, setCameraId] = useState<string | null>(initialCameraId ?? null);
  // The create flow is a two-step wizard gated by the "test before you create" step; editing
  // shows every field on one page instead, since the camera already exists and was already
  // tested.
  const showConnectFields = step === 'connect' || isEditFlow;
  const showDetailsFields = step === 'details' || isEditFlow;
  const [credentialTest, setCredentialTest] = useState<CredentialTestState>({ status: 'idle' });
  // '' means "enter a username/password manually below"; otherwise the chosen saved-credential
  // library entry's reference, which the camera is pointed at directly rather than getting its
  // own sealed copy — see toCameraWriteRequest's savedCredentialReference parameter. Seeded from
  // the camera being edited, if any, so its already-shared credential shows as selected instead
  // of the picker defaulting to "Enter manually below" regardless of what's actually set.
  const [savedCredentialReference, setSavedCredentialReference] = useState(initialCredentialReference ?? '');
  const { register, formState: { errors, isSubmitting } } = form;
  const [
    latitudeValue, longitudeValue, azimuthValue, protocolValue, ipAddressValue, portValue,
    organizationUnitIdValue, geographicAreaIdValue, cameraCodeValue, nameValue, cameraTypeValue,
  ] = useWatch({
    control: form.control,
    name: ['latitude', 'longitude', 'azimuth', 'protocol', 'ipAddress', 'port', 'organizationUnitId', 'geographicAreaId', 'cameraCode', 'name', 'cameraType'],
  });
  const latitude = coordinate(latitudeValue, -90, 90);
  const longitude = coordinate(longitudeValue, -180, 180);
  const azimuth = optionalNumericValue(azimuthValue);
  const selectedOrganizationId = form.watch('organizationId');
  const selectedVmsId = form.watch('vmsId');
  const selectedStreamPreference = form.watch('streamPreference');
  const organizationRegistration = register('organizationId');
  const validationProps = (name: keyof CameraFormValues, statusId?: string) => {
    const describedBy = [errors[name] ? `${name}-error` : undefined, statusId].filter(Boolean).join(' ') || undefined;
    return { 'aria-describedby': describedBy, 'aria-invalid': Boolean(errors[name]) };
  };
  const numericFields: Array<{ name: NumericField; label: string; step?: string }> = [
    { name: 'altitude', label: 'Altitude', step: 'any' }, { name: 'mountingHeight', label: 'Mounting height', step: 'any' },
    { name: 'tilt', label: 'Tilt', step: 'any' },
    { name: 'horizontalFov', label: 'Horizontal field of view', step: 'any' }, { name: 'verticalFov', label: 'Vertical field of view', step: 'any' },
    { name: 'effectiveRange', label: 'Effective range', step: 'any' },
  ];
  const readyForTest = Boolean(
    cameraCodeValue?.trim() && nameValue?.trim() && organizationUnitIdValue && geographicAreaIdValue && cameraTypeValue
      && protocolValue && ipAddressValue?.trim() && portValue?.trim() && latitude !== null && longitude !== null,
  );

  useEffect(() => {
    onCoordinatesChange?.(latitude, longitude);
  }, [latitude, longitude, onCoordinatesChange]);

  // The "Test connection" step already creates a partial camera row server-side, so an abandoned
  // registration is doubly costly — lost typing on top of an orphaned record. Persisting a debounced
  // draft to localStorage (create flow only) at least saves the typing.
  useEffect(() => {
    if (!isCreateFlow) return undefined;
    let saveTimeout: number | undefined;
    const subscription = form.watch((values) => {
      window.clearTimeout(saveTimeout);
      saveTimeout = window.setTimeout(() => {
        const { password: _password, ...draft } = values as CameraFormValues;
        try {
          window.localStorage.setItem(CAMERA_DRAFT_STORAGE_KEY, JSON.stringify(draft));
        } catch {
          // localStorage unavailable (private browsing, quota) — draft persistence is best-effort.
        }
      }, 400);
    });
    return () => {
      window.clearTimeout(saveTimeout);
      subscription.unsubscribe();
    };
  }, [form, isCreateFlow]);

  function continueDraft() {
    if (storedDraft) form.reset({ ...defaults, ...storedDraft });
    setDraftBannerVisible(false);
  }

  function discardDraft() {
    clearCameraDraft();
    setDraftBannerVisible(false);
  }

  /**
   * Creates the camera (first attempt) or saves the latest connect-step fields to the
   * already-created camera (a retry after fixing ip/port/protocol/credential), then saves
   * whatever credential was entered and runs the server-side credential test. The camera is
   * created here — before the operator has filled in the rest of the form — so the test can run
   * against a real row; an abandoned registration therefore leaves a partially-filled camera,
   * which is an accepted trade-off (see NewCameraPage's onSubmit for where the remaining fields
   * are saved via PATCH once the operator finishes).
   */
  async function runCredentialTest() {
    const valid = await form.trigger(requiredForTestFields);
    if (!valid) return;
    setCredentialTest({ status: 'testing' });
    try {
      const values = form.getValues();
      let id = cameraId;
      if (!id) {
        const created = await api.cameras.create(toCameraWriteRequest(values, savedCredentialReference));
        id = created.id;
        setCameraId(id);
      } else {
        await api.cameras.update(id, toCameraPatchRequest(values));
        if (savedCredentialReference) {
          await api.cameras.update(id, { credentialReference: savedCredentialReference });
        }
      }
      // A saved credential is already sealed under its own reference (AddSavedCredentialModal) —
      // pointing the camera at it above is enough. Only a manually-entered username/password
      // needs sealing here, and only when no saved credential is selected.
      if (!savedCredentialReference) {
        const credential = toCameraCredentialInput(values);
        if (credential.username || credential.password) {
          await api.cameras.credentials.save(id, credential);
        }
      }
      const result = await api.cameras.testCredential(id);
      const report = result.result;
      if (!report) {
        setCredentialTest({ status: 'error', detail: result.failureReason ?? 'The credential check did not complete. Please try again.' });
        return;
      }
      if (report.authOutcome === 'authenticated') {
        setCredentialTest({ status: 'authenticated' });
        setStep('details');
      } else if (report.authOutcome === 'not_verifiable') {
        setCredentialTest({ status: 'not_verifiable', detail: report.detail ?? 'The credential could not be verified for this protocol, but the device is reachable.' });
      } else if (report.authOutcome === 'credential_rejected') {
        setCredentialTest({ status: 'credential_rejected', detail: report.detail ?? 'The device rejected this credential.' });
      } else {
        setCredentialTest({ status: 'unreachable', detail: report.detail ?? result.failureReason ?? 'The device did not respond.' });
      }
    } catch (error) {
      setCredentialTest({ status: 'error', detail: isApiProblem(error) ? error.detail : 'Unable to test the connection. Please try again.' });
    }
  }

  return (
    <form className="camera-form" onSubmit={form.handleSubmit(async (values) => {
      form.clearErrors('root');
      if (!cameraId) return;
      try {
        await onSubmit(cameraId, toCameraPatchRequest(values));
        if (isCreateFlow) clearCameraDraft();
      } catch (error) {
        form.setError('root', { message: isApiProblem(error) ? error.detail : 'Unable to save the camera. Please try again.' });
      }
    })}>
      {draftBannerVisible && <div className="camera-form-draft-banner" role="status">
        <p>You have an unfinished camera registration saved in this browser. Continue where you left off, or start over.</p>
        <div className="camera-form-draft-banner__actions">
          <Button type="button" onClick={continueDraft}>Continue draft</Button>
          <button className="button button--secondary" type="button" onClick={discardDraft}>Start over</button>
        </div>
      </div>}
      {!isEditFlow && <ol className="camera-form-steps" aria-label="Registration steps">
        <li aria-current={step === 'connect' ? 'step' : undefined}>1. Connect</li>
        <li aria-current={step === 'details' ? 'step' : undefined}>2. Camera details</li>
      </ol>}
      {/* Editing shows every field on one page — there's no "test before you create" gate to walk
          through, so nothing is hidden behind the create flow's two-step wizard. */}
      {showConnectFields && <fieldset>
        <legend>Connect to the device</legend>
        {!isEditFlow && <p className="field-help">Identify the camera and enter how to reach it on the network, then test the connection — this creates the camera and verifies its credential before you continue.</p>}
        <label><span>Camera code<span aria-hidden="true"> *</span></span><input aria-required="true" autoFocus={!isEditFlow} disabled={isEditFlow} {...register('cameraCode')} {...validationProps('cameraCode')} /></label><FieldError id="cameraCode-error" message={errors.cameraCode?.message} />
        {isEditFlow && <p className="field-help">Camera code cannot be changed after registration.</p>}
        <label><span>Name<span aria-hidden="true"> *</span></span><input aria-required="true" {...register('name')} {...validationProps('name')} /></label><FieldError id="name-error" message={errors.name?.message} />
        <label>Organization<select aria-describedby={selectorStates.organizations.state === 'ready' ? undefined : 'organizations-status'} disabled={selectorStates.organizations.state !== 'ready'} {...organizationRegistration} onChange={(event) => {
          organizationRegistration.onChange(event);
          form.setValue('organizationUnitId', '');
          form.clearErrors('organizationUnitId');
          onOrganizationChange(event.target.value);
        }}><option value="">Select an organization</option>{organizations.map((organization) => <option key={organization.id} value={organization.id}>{organization.name} ({organization.code})</option>)}</select></label>
        <SelectorStatus id="organizations-status" label="Organizations" selector={selectorStates.organizations} emptyMessage="No organizations are available. Ask an administrator to create an organization before registering a camera." />
        <TreeSelect
          id="organizationUnitId"
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
          describedBy={[errors.organizationUnitId ? 'organizationUnitId-error' : undefined, selectorStates.organizationUnits.state === 'ready' ? undefined : 'organization-units-status'].filter(Boolean).join(' ') || undefined}
          placeholder="Select an organization unit"
          emptyMessage="This organization has no units."
        />
        <FieldError id="organizationUnitId-error" message={errors.organizationUnitId?.message} />
        <SelectorStatus id="organization-units-status" label="Organization units" selector={selectorStates.organizationUnits} idleMessage="Select an organization to load its organization units." emptyMessage="No organization units are available for this organization. Choose another organization or ask an administrator to create an organization unit." />
        <TreeSelect
          id="geographicAreaId"
          label="Geographic area"
          items={geographicAreas}
          getParentId={(area) => area.parentAreaId}
          value={geographicAreaIdValue || undefined}
          onChange={(areaId) => form.setValue('geographicAreaId', areaId ?? '', { shouldDirty: true, shouldValidate: true })}
          disabled={selectorStates.geographicAreas.state !== 'ready'}
          loading={selectorStates.geographicAreas.state === 'loading'}
          error={selectorStates.geographicAreas.state === 'error'}
          required
          invalid={Boolean(errors.geographicAreaId)}
          describedBy={[errors.geographicAreaId ? 'geographicAreaId-error' : undefined, selectorStates.geographicAreas.state === 'ready' ? undefined : 'geographic-areas-status'].filter(Boolean).join(' ') || undefined}
          placeholder="Select a geographic area"
          emptyMessage="No geographic areas are available."
        />
        <FieldError id="geographicAreaId-error" message={errors.geographicAreaId?.message} />
        <SelectorStatus id="geographic-areas-status" label="Geographic areas" selector={selectorStates.geographicAreas} emptyMessage="No geographic areas are available. Ask an administrator to create one before registering a camera." />
        <label><span>Camera type<span aria-hidden="true"> *</span></span><select aria-required="true" {...register('cameraType')} {...validationProps('cameraType')}><option value="">Select a camera type</option>{cameraTypes.map((type) => <option key={type} value={type}>{type}</option>)}</select></label><FieldError id="cameraType-error" message={errors.cameraType?.message} />
        <label><span>Latitude<span aria-hidden="true"> *</span></span><input aria-required="true" inputMode="decimal" {...register('latitude')} {...validationProps('latitude')} /></label><FieldError id="latitude-error" message={errors.latitude?.message} />
        <label><span>Longitude<span aria-hidden="true"> *</span></span><input aria-required="true" inputMode="decimal" {...register('longitude')} {...validationProps('longitude')} /></label><FieldError id="longitude-error" message={errors.longitude?.message} />
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
        <label><span>Protocol<span aria-hidden="true"> *</span></span><select aria-required={!selectedVmsId} {...register('protocol')} {...validationProps('protocol')}><option value="">Not recorded</option>{protocols.map((protocol) => <option key={protocol} value={protocol}>{protocol}</option>)}</select></label><FieldError id="protocol-error" message={errors.protocol?.message} />
        <label><span>IP address<span aria-hidden="true"> *</span></span><input aria-required={!selectedVmsId} {...register('ipAddress')} {...validationProps('ipAddress')} /></label><FieldError id="ipAddress-error" message={errors.ipAddress?.message} />
        <label><span>Port<span aria-hidden="true"> *</span></span><input aria-required={!selectedVmsId} type="number" step="1" {...register('port')} {...validationProps('port')} /></label><FieldError id="port-error" message={errors.port?.message} />
        <label>Stream reference<input {...register('streamReference')} {...validationProps('streamReference')} /></label>
        <label>Stream preference<select {...register('streamPreference')} {...validationProps('streamPreference')}>{streamPreferences.map((preference) => <option key={preference} value={preference}>{preference}</option>)}</select></label>
        <FieldError id="streamPreference-error" message={errors.streamPreference?.message} />
        {selectedStreamPreference === 'HLS' && <>
          <label><span>Native HLS URL<span aria-hidden="true"> *</span></span><input aria-required="true" placeholder="https://camera/hls/stream.m3u8" {...register('nativeHlsUrl')} {...validationProps('nativeHlsUrl')} /></label>
          <FieldError id="nativeHlsUrl-error" message={errors.nativeHlsUrl?.message} />
        </>}
        {selectedStreamPreference === 'WEBRTC' && <>
          <label><span>Native WebRTC (WHEP) URL<span aria-hidden="true"> *</span></span><input aria-required="true" placeholder="https://camera/whep" {...register('nativeWebrtcUrl')} {...validationProps('nativeWebrtcUrl')} /></label>
          <FieldError id="nativeWebrtcUrl-error" message={errors.nativeWebrtcUrl?.message} />
        </>}
        <CredentialLibraryPicker
          value={savedCredentialReference}
          onSelect={(reference) => {
            setSavedCredentialReference(reference);
            if (reference) {
              form.setValue('username', '', { shouldDirty: true });
              form.setValue('password', '', { shouldDirty: true });
            }
          }}
        />
        {!savedCredentialReference && <>
          <label>Username<input autoComplete="username" {...register('username')} /></label>
          <label>Password<input autoComplete="new-password" type="password" {...register('password')} /></label>
        </>}
        <p className="field-help">{savedCredentialReference ? 'This camera will share the selected saved credential — rotating or deleting it later changes every camera that uses it.' : 'The credential is sealed to this camera once the connection test runs — it is never stored as plain text or shown again.'}</p>
        <div className="connection-check">
          <button className="button button--secondary" disabled={!readyForTest || credentialTest.status === 'testing'} type="button" onClick={() => { void runCredentialTest(); }}>
            {credentialTest.status === 'testing' ? 'Testing connection…' : 'Test connection'}
          </button>
          <p className="field-help">{isEditFlow ? 'Saves these network fields and checks that the device authenticates at this address.' : 'Creates the camera, saves the credential, and checks that the device authenticates at this address.'}</p>
          {credentialTest.status === 'authenticated' && <p role="status"><StatusBadge tone="success">Authenticated</StatusBadge></p>}
          {credentialTest.status === 'not_verifiable' && <div role="status">
            <p><StatusBadge tone="warning">Reachable — credential not verified</StatusBadge> {credentialTest.detail}</p>
            {!isEditFlow && <Button type="button" onClick={() => setStep('details')}>Continue anyway</Button>}
          </div>}
          {credentialTest.status === 'credential_rejected' && <p className="form-error" role="alert"><StatusBadge tone="danger">Credential rejected</StatusBadge> {credentialTest.detail}</p>}
          {credentialTest.status === 'unreachable' && <p className="form-error" role="alert"><StatusBadge tone="danger">Not reachable</StatusBadge> {credentialTest.detail}</p>}
          {credentialTest.status === 'error' && <p className="form-error" role="alert">{credentialTest.detail}</p>}
        </div>
      </fieldset>}
      {showDetailsFields && <div>
        {!isEditFlow && <button className="button button--secondary" type="button" onClick={() => setStep('connect')}>Back to connection details</button>}
        <fieldset><legend>Device</legend>
          <label><span>Manufacturer<span aria-hidden="true"> *</span></span><input aria-required={!selectedVmsId} {...register('manufacturer')} {...validationProps('manufacturer')} /></label><FieldError id="manufacturer-error" message={errors.manufacturer?.message} />
          <label>Model<input {...register('model')} {...validationProps('model')} /></label>
          <label>Serial number<input {...register('serialNumber')} {...validationProps('serialNumber')} /></label>
          <label className="checkbox-label"><input {...register('recordEvents')} type="checkbox" /> Save camera events</label>
        </fieldset>
        <button aria-controls="camera-additional-details" aria-expanded={detailsOpen} className="button button--secondary" onClick={() => setDetailsOpen((open) => !open)} type="button">Additional details</button>
        {detailsOpen && <section id="camera-additional-details" aria-label="Additional details">
          <fieldset><legend>VMS link</legend>
            <label>VMS<select disabled={selectorStates.vms.state !== 'ready'} {...register('vmsId')} {...validationProps('vmsId', selectorStates.vms.state === 'ready' ? undefined : 'vms-status')}><option value="">Manual registration</option>{vms.map((vmsTarget) => <option key={vmsTarget.id} value={vmsTarget.id}>{vmsTarget.displayName} ({vmsTarget.code})</option>)}</select></label><FieldError id="vmsId-error" message={errors.vmsId?.message} />
            <SelectorStatus id="vms-status" label="VMS records" selector={selectorStates.vms} emptyMessage="No VMS records are available. Continue with manual registration, or ask an administrator to create a VMS record." />
          </fieldset>
          <fieldset><legend>Position, optics, and status</legend>
            {numericFields.map(({ name, label, step: fieldStep }) => <div key={name}><label>{label}<input type="number" step={fieldStep} {...register(name)} {...validationProps(name)} /></label><FieldError id={`${name}-error`} message={errors[name]?.message} /></div>)}
            <label>Installation date<input type="date" {...register('installationDate')} {...validationProps('installationDate')} /></label>
            <label>Operational status<select {...register('operationalStatus')} {...validationProps('operationalStatus')}><option value="">Use server default</option>{operationalStatuses.map((status) => <option key={status} value={status}>{status}</option>)}</select></label><FieldError id="operationalStatus-error" message={errors.operationalStatus?.message} />
            <label>Connectivity status<select {...register('connectivityStatus')} {...validationProps('connectivityStatus')}><option value="">Use server default</option>{connectivityStatuses.map((status) => <option key={status} value={status}>{status}</option>)}</select></label><FieldError id="connectivityStatus-error" message={errors.connectivityStatus?.message} />
            <label>Maintenance status<select {...register('maintenanceStatus')} {...validationProps('maintenanceStatus')}><option value="">Use server default</option>{maintenanceStatuses.map((status) => <option key={status} value={status}>{status}</option>)}</select></label><FieldError id="maintenanceStatus-error" message={errors.maintenanceStatus?.message} />
          </fieldset>
        </section>}
        <FieldError id="camera-form-error" message={errors.root?.message} />
        <Button disabled={isSubmitting} type="submit">{isSubmitting ? 'Saving…' : 'Save details'}</Button>
      </div>}
    </form>
  );
}
