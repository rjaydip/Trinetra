import { apiBaseUrl } from '../config/env';
import { clearSession, readSession } from '../auth/session';
import { toastBridge } from '../components/Toast';

import type { ApiProblemShape } from './models';

const SAFE_FAILURE_DETAIL = 'The service could not complete this request. Please try again.';

export class ApiProblem extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail: string;
  readonly errors?: Record<string, string[]>;

  constructor({ status, title, detail, errors }: Required<Pick<ApiProblem, 'status' | 'title' | 'detail'>> & Pick<ApiProblem, 'errors'>) {
    super(detail);
    this.name = 'ApiProblem';
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.errors = errors;
  }
}

export { apiBaseUrl };

function withBearerToken(init: RequestInit): RequestInit {
  const headers = new Headers(init.headers);
  const session = readSession();

  headers.set('accept', 'application/json');
  if (session) headers.set('authorization', `Bearer ${session.token}`);
  if (init.body && !(init.body instanceof FormData) && !headers.has('content-type')) {
    headers.set('content-type', 'application/json');
  }

  return { ...init, headers };
}

function problemFromShape(status: number, body: ApiProblemShape | null): ApiProblem {
  return new ApiProblem({
    status,
    title: typeof body?.title === 'string' && body.title ? body.title : 'Request failed',
    detail: typeof body?.detail === 'string' && body.detail ? body.detail : SAFE_FAILURE_DETAIL,
    errors: body?.errors,
  });
}

export async function problemFrom(response: Response): Promise<ApiProblem> {
  let body: ApiProblemShape | null = null;
  const contentType = response.headers.get('content-type') ?? '';

  if (contentType.includes('json')) {
    try {
      body = await response.json() as ApiProblemShape;
    } catch {
      body = null;
    }
  }

  return problemFromShape(response.status, body);
}

export function isApiProblem(error: unknown): error is ApiProblem {
  return error instanceof ApiProblem;
}

/** The one place every page/component turns a caught error into safe, user-facing text. */
export function errorDetail(error: unknown, fallback: string): string {
  return isApiProblem(error) ? error.detail : fallback;
}

export async function request<T>(path: string, init: RequestInit = {}, signal?: AbortSignal): Promise<T> {
  let response: Response;
  const authorizedInit = withBearerToken(signal ? { ...init, signal } : init);

  try {
    response = await fetch(`${apiBaseUrl()}${path}`, authorizedInit);
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') throw err;
    toastBridge?.error('The service could not be reached. Please try again.');
    throw new ApiProblem({
      status: 0,
      title: 'Service unavailable',
      detail: 'The service could not be reached. Please try again.',
    });
  }

  if (!response.ok) {
    const activeSession = readSession();
    // An in-flight request from a previous login must not expire a new login.
    if (response.status === 401 && activeSession
      && new Headers(authorizedInit.headers).get('authorization') === `Bearer ${activeSession.token}`) {
      clearSession();
      toastBridge?.info('Your session ended. Please sign in again.');
    }
    throw await problemFrom(response);
  }

  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

/** Like `request`, but for a binary response (an evidence image) rather than JSON — a plain
 * `<img src="...">` can't carry the `Authorization` header this route requires, so the caller
 * fetches the bytes itself and renders them via `URL.createObjectURL`. */
export async function requestBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  const authorizedInit = withBearerToken(signal ? { signal } : {});
  let response: Response;

  try {
    response = await fetch(`${apiBaseUrl()}${path}`, authorizedInit);
  } catch (err) {
    if (err instanceof DOMException && err.name === 'AbortError') throw err;
    toastBridge?.error('The service could not be reached. Please try again.');
    throw new ApiProblem({
      status: 0,
      title: 'Service unavailable',
      detail: 'The service could not be reached. Please try again.',
    });
  }

  if (!response.ok) {
    throw await problemFrom(response);
  }

  return response.blob();
}
