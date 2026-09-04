import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';

import { isApiProblem } from '../../api/client';
import { api } from '../../api/endpoints';
import type { ConnectionTestResult, CredentialRequest } from '../../api/models';
import { StatusBadge } from '../../components/ui';

interface SafeTestDetails {
  reachable?: boolean;
  connectMs?: number;
  capabilities?: string;
  cameraCount?: number;
  clockSkewSeconds?: number;
  warnings?: string[];
  failure?: string;
  failureKind?: string;
}

type SafeConnectionTestResult = Omit<ConnectionTestResult, 'result'> & { result: SafeTestDetails | null };

function safeTestDetails(result: unknown): SafeTestDetails | null {
  if (!result || typeof result !== 'object' || Array.isArray(result)) return null;
  const source = result as Record<string, unknown>;
  const safe: SafeTestDetails = {};
  if (typeof source.reachable === 'boolean') safe.reachable = source.reachable;
  if (typeof source.connectMs === 'number') safe.connectMs = source.connectMs;
  if (typeof source.capabilities === 'string') safe.capabilities = source.capabilities;
  if (typeof source.cameraCount === 'number') safe.cameraCount = source.cameraCount;
  if (typeof source.clockSkewSeconds === 'number') safe.clockSkewSeconds = source.clockSkewSeconds;
  if (Array.isArray(source.warnings)) safe.warnings = source.warnings.filter((warning): warning is string => typeof warning === 'string');
  if (typeof source.failure === 'string') safe.failure = source.failure;
  if (typeof source.failureKind === 'string') safe.failureKind = source.failureKind;
  return safe;
}

function sanitizeTestResult(result: ConnectionTestResult): SafeConnectionTestResult {
  return {
    testId: result.testId,
    targetId: result.targetId,
    status: result.status,
    requestedAt: result.requestedAt,
    completedAt: result.completedAt,
    failureReason: result.failureReason,
    result: safeTestDetails(result.result),
  };
}

function messageFrom(error: unknown, fallback: string) {
  return isApiProblem(error) ? error.detail : fallback;
}

function formatDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function testInProgress(status?: string) {
  return status?.toLowerCase() === 'pending' || status?.toLowerCase() === 'running';
}

function ConnectionResult({ test }: { test: SafeConnectionTestResult }) {
  const status = test.status.toLowerCase();
  const tone = status === 'succeeded' || status === 'success' || status === 'completed' ? 'success' : testInProgress(status) ? 'warning' : 'danger';
  return <div className="connection-result" aria-live="polite">
    <StatusBadge tone={tone}>Connection test {test.status}</StatusBadge>
    {test.completedAt && <p>Completed {formatDate(test.completedAt)}</p>}
    {test.failureReason && <p className="form-error">{test.failureReason}</p>}
    {test.result && <dl>
      {test.result.reachable !== undefined && <div><dt>Reachable</dt><dd>{test.result.reachable ? 'Yes' : 'No'}</dd></div>}
      {test.result.connectMs !== undefined && <div><dt>Connect time</dt><dd>{Math.round(test.result.connectMs)} ms</dd></div>}
      {test.result.cameraCount !== undefined && <div><dt>Cameras reported</dt><dd>{test.result.cameraCount}</dd></div>}
      {test.result.capabilities && <div><dt>Capabilities</dt><dd>{test.result.capabilities}</dd></div>}
    </dl>}
    {test.result?.warnings?.length ? <ul>{test.result.warnings.map((warning) => <li key={warning}>{warning}</li>)}</ul> : null}
  </div>;
}

export function CredentialPanel({ vmsId, permissions }: { vmsId: string; permissions: string[] }) {
  const queryClient = useQueryClient();
  const canWrite = permissions.includes('credential.write');
  const canTest = permissions.includes('integration.manage');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [token, setToken] = useState('');
  const [description, setDescription] = useState('');
  const [savePending, setSavePending] = useState(false);
  const [saveError, setSaveError] = useState('');
  const [updatedAt, setUpdatedAt] = useState('');
  const [confirmedExists, setConfirmedExists] = useState(false);
  const [testId, setTestId] = useState('');
  const [testStarting, setTestStarting] = useState(false);
  const [testError, setTestError] = useState('');

  const credentialStatus = useQuery({
    queryKey: ['vms', vmsId, 'credential-status'],
    queryFn: () => api.credentials.status(vmsId),
  });
  const connectionTest = useQuery({
    queryKey: ['vms', vmsId, 'connection-test', testId],
    enabled: Boolean(testId),
    queryFn: async () => sanitizeTestResult(await api.connectionTests.get(vmsId, testId)),
    refetchInterval: (query) => testInProgress(query.state.data?.status) ? 1_000 : false,
  });

  const credentialExists = confirmedExists || credentialStatus.data?.exists === true;

  async function saveCredential(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaveError('');
    setUpdatedAt('');
    if (!password.length && !token.length) {
      setSaveError('A password or token is required.');
      return;
    }
    setSavePending(true);
    const request: CredentialRequest = {
      username: username.trim() || undefined,
      password: password || undefined,
      token: token || undefined,
      description: description.trim() || undefined,
    };
    try {
      const response = await api.credentials.save(vmsId, request);
      setConfirmedExists(true);
      setUpdatedAt(response.updatedAt);
      await queryClient.invalidateQueries({ queryKey: ['vms', vmsId, 'credential-status'] });
    } catch (error) {
      setSaveError(messageFrom(error, 'Unable to save the credential. Please try again.'));
    } finally {
      request.password = undefined;
      request.token = undefined;
      setPassword('');
      setToken('');
      setSavePending(false);
    }
  }

  async function startConnectionTest() {
    setTestError('');
    setTestId('');
    setTestStarting(true);
    try {
      const accepted = await api.connectionTests.create(vmsId);
      setTestId(accepted.testId);
    } catch (error) {
      setTestError(messageFrom(error, 'Unable to start the connection test. Please try again.'));
    } finally {
      setTestStarting(false);
    }
  }

  return <section className="credential-panel" aria-labelledby="credential-title">
    <header>
      <div><p className="eyebrow">Secure access</p><h2 id="credential-title">Device credential</h2></div>
      {credentialStatus.isPending ? <span role="status">Checking credential status…</span>
        : credentialStatus.isError ? <span className="form-error" role="alert">{messageFrom(credentialStatus.error, 'Credential status is unavailable.')}</span>
          : <StatusBadge tone={credentialExists ? 'success' : 'warning'}>{credentialExists ? 'Credential set' : 'Credential not set'}</StatusBadge>}
    </header>
    <p>Stored credentials are write-only. Saved passwords and tokens cannot be viewed again here.</p>
    {canWrite && <form className="credential-form" onSubmit={saveCredential}>
      <label>Username<input autoComplete="username" value={username} onChange={(event) => setUsername(event.target.value)} /></label>
      <label>Password<input autoComplete="new-password" type="password" value={password} onChange={(event) => setPassword(event.target.value)} /></label>
      <label>Token<input autoComplete="off" type="password" value={token} onChange={(event) => setToken(event.target.value)} /></label>
      <label>Description<input value={description} onChange={(event) => setDescription(event.target.value)} /></label>
      {saveError && <p className="form-error" role="alert">{saveError}</p>}
      {updatedAt && <p className="save-confirmation" role="status">Credential updated {formatDate(updatedAt)}. Secret fields were cleared.</p>}
      <button className="button" disabled={savePending} type="submit">{savePending ? 'Saving credential…' : 'Save credential'}</button>
    </form>}
    {canTest && <div className="connection-test">
      <div><h3>Connection test</h3><p>Uses the stored credential to contact the live device. It may take up to one minute.</p></div>
      <button className="button button--secondary" disabled={testStarting || testInProgress(connectionTest.data?.status)} type="button" onClick={startConnectionTest}>{testStarting ? 'Starting test…' : 'Test connection'}</button>
      {testId && (!connectionTest.data || testInProgress(connectionTest.data.status)) && <p role="status">Connection test {connectionTest.data?.status ?? 'pending'}…</p>}
      {connectionTest.isError && <p className="form-error" role="alert">{messageFrom(connectionTest.error, 'The connection test result could not be loaded.')}</p>}
      {testError && <p className="form-error" role="alert">{testError}</p>}
      {connectionTest.data && !testInProgress(connectionTest.data.status) && <ConnectionResult test={connectionTest.data} />}
    </div>}
  </section>;
}
