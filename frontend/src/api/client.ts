import { clearSession, readSession } from '../auth/session';

import type { ApiProblemShape } from './models';

const DEFAULT_BASE_URL = 'http://localhost:5261';
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

export function apiBaseUrl(): string {
  return (import.meta.env.VITE_API_BASE_URL || DEFAULT_BASE_URL).replace(/\/$/, '');
}

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

export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  let response: Response;
  const authorizedInit = withBearerToken(init);

  try {
    response = await fetch(`${apiBaseUrl()}${path}`, authorizedInit);
  } catch {
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
      && new Headers(authorizedInit.headers).get('authorization') === `Bearer ${activeSession.token}`) clearSession();
    throw await problemFrom(response);
  }

  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}
