export type ErrorCode =
  | 'invalid_credentials' | 'too_many_attempts' | 'invalid_link' | 'id_taken' | 'invalid_gom_id' | 'password_too_short'
  | 'password_too_long' | 'password_too_common' | 'invalid_display_name' | 'missing_fields' | 'bad_origin'
  | 'unauthorized' | 'forbidden' | 'network' | 'server' | 'unknown';

const SERVER_CODES: ReadonlySet<string> = new Set<ErrorCode>([
  'invalid_credentials', 'too_many_attempts', 'invalid_link', 'id_taken', 'invalid_gom_id', 'password_too_short',
  'password_too_long', 'password_too_common', 'invalid_display_name', 'missing_fields', 'bad_origin',
]);

export interface Failure { ok: false; status: number; error: ErrorCode; retryAfter?: number }
export type Result<T> = { ok: true; data: T } | Failure;

export interface User { gomId: string; displayName: string; isOwner: boolean }
export interface SessionInfo { id: string; deviceLabel: string; createdAt: string; lastSeenAt: string; current: boolean }
export interface Invite { id: string; createdAt: string; expiresAt: string; usedBy: string | null; status: 'pending' | 'used' | 'revoked' | 'expired' }
export interface Member { gomId: string; displayName: string; isOwner: boolean; disabled: boolean; createdAt: string }
export interface Issued { id?: string; token: string; path: string; expiresAt: string }
export interface JoinBody { gomId: string; password: string; displayName: string }

export interface Api {
  me(): Promise<Result<User>>;
  signIn(gomId: string, password: string): Promise<Result<User>>;
  signOut(): Promise<Result<void>>;
  peekInvite(token: string): Promise<Result<{ invitedBy: string }>>;
  join(token: string, body: JoinBody): Promise<Result<User>>;
  peekReset(token: string): Promise<Result<{ gomId: string; displayName: string }>>;
  reset(token: string, password: string): Promise<Result<User>>;
  sessions(): Promise<Result<SessionInfo[]>>;
  endSession(id: string): Promise<Result<void>>;
  createInvite(): Promise<Result<Issued>>;
  invites(): Promise<Result<Invite[]>>;
  revokeInvite(id: string): Promise<Result<void>>;
  members(): Promise<Result<Member[]>>;
  resetLink(gomId: string): Promise<Result<Issued>>;
}

/** The browser sends the Origin header itself on same-origin POST and DELETE; the server checks it. */
export function createApi(fetchFn: typeof fetch = (input, init) => fetch(input, init)): Api {
  async function call<T>(method: string, path: string, body?: unknown): Promise<Result<T>> {
    let response: Response;
    try {
      response = await fetchFn(path, {
        method,
        credentials: 'same-origin',
        ...(body === undefined ? {} : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
      });
    } catch {
      return { ok: false, status: 0, error: 'network' };
    }
    if (response.ok) {
      if (response.status === 204) return { ok: true, data: undefined as T };
      try { return { ok: true, data: (await response.json()) as T }; } catch { return { ok: false, status: response.status, error: 'server' }; }
    }
    return failure(response);
  }

  const enc = encodeURIComponent;
  return {
    me: () => call('GET', '/api/me'),
    signIn: (gomId, password) => call('POST', '/api/session', { gomId, password }),
    signOut: () => call('DELETE', '/api/session'),
    peekInvite: (token) => call('GET', `/api/join/${enc(token)}`),
    join: (token, body) => call('POST', `/api/join/${enc(token)}`, body),
    peekReset: (token) => call('GET', `/api/reset/${enc(token)}`),
    reset: (token, password) => call('POST', `/api/reset/${enc(token)}`, { password }),
    sessions: () => call('GET', '/api/sessions'),
    endSession: (id) => call('DELETE', `/api/sessions/${enc(id)}`),
    createInvite: () => call('POST', '/api/invites'),
    invites: () => call('GET', '/api/invites'),
    revokeInvite: (id) => call('DELETE', `/api/invites/${enc(id)}`),
    members: () => call('GET', '/api/users'),
    resetLink: (gomId) => call('POST', `/api/users/${enc(gomId)}/reset-link`),
  };
}

async function failure(response: Response): Promise<Failure> {
  let code: string | undefined;
  try { code = ((await response.json()) as { error?: unknown }).error as string | undefined; } catch { /* no JSON body */ }

  const error: ErrorCode =
    code !== undefined && SERVER_CODES.has(code) ? (code as ErrorCode) :
    response.status === 401 ? 'unauthorized' :
    response.status === 403 ? 'forbidden' :
    response.status >= 500 ? 'server' : 'unknown';

  const retry = Number.parseInt(response.headers.get('Retry-After') ?? '', 10);
  return Number.isFinite(retry)
    ? { ok: false, status: response.status, error, retryAfter: retry }
    : { ok: false, status: response.status, error };
}
