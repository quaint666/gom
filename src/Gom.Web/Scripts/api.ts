export type ErrorCode =
  | 'invalid_credentials' | 'too_many_attempts' | 'invalid_link' | 'id_taken' | 'invalid_gom_id' | 'password_too_short'
  | 'password_too_long' | 'password_too_common' | 'invalid_display_name' | 'missing_fields' | 'bad_origin'
  | 'wrong_password' | 'cannot_disable_owner' | 'user_not_found' | 'cannot_add_self' | 'already_friends' | 'you_blocked_them'
  | 'not_friends' | 'cannot_chat_self' | 'unauthorized' | 'forbidden' | 'network' | 'server' | 'unknown';

const SERVER_CODES: ReadonlySet<string> = new Set<ErrorCode>([
  'invalid_credentials', 'too_many_attempts', 'invalid_link', 'id_taken', 'invalid_gom_id', 'password_too_short',
  'password_too_long', 'password_too_common', 'invalid_display_name', 'missing_fields', 'bad_origin', 'wrong_password', 'cannot_disable_owner',
  'user_not_found', 'cannot_add_self', 'already_friends', 'you_blocked_them', 'not_friends', 'cannot_chat_self',
]);

export interface Failure { ok: false; status: number; error: ErrorCode; retryAfter?: number }
export type Result<T> = { ok: true; data: T } | Failure;

export interface User { gomId: string; displayName: string; isOwner: boolean }
export interface SessionInfo { id: string; deviceLabel: string; createdAt: string; lastSeenAt: string; current: boolean }
export interface Invite { id: string; createdAt: string; expiresAt: string; usedBy: string | null; status: 'pending' | 'used' | 'revoked' | 'expired' }
export interface Member { gomId: string; displayName: string; isOwner: boolean; disabled: boolean; createdAt: string }
export interface Issued { id?: string; token: string; path: string; expiresAt: string }
export interface JoinBody { gomId: string; password: string; displayName: string }

export interface Person { gomId: string; displayName: string }
export interface FriendRequest extends Person { createdAt: string }
export interface FriendLists { friends: Person[]; incoming: FriendRequest[]; outgoing: FriendRequest[] }
export interface Conversation {
  id: string; kind: 'direct' | 'group'; title: string | null; peer: Person | null; lastSeq: number; lastReadSeq: number;
}

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
  setNickname(displayName: string): Promise<Result<User>>;
  changePassword(current: string, password: string): Promise<Result<void>>;
  disableMember(gomId: string): Promise<Result<void>>;
  enableMember(gomId: string): Promise<Result<void>>;
  friends(): Promise<Result<FriendLists>>;
  askFriend(gomId: string): Promise<Result<{ status: 'pending' | 'friends' }>>;
  acceptFriend(gomId: string): Promise<Result<void>>;
  /** Declines a request sent to you, or cancels one you sent. */
  withdrawFriend(gomId: string): Promise<Result<void>>;
  unfriend(gomId: string): Promise<Result<void>>;
  blocks(): Promise<Result<Person[]>>;
  block(gomId: string): Promise<Result<void>>;
  unblock(gomId: string): Promise<Result<void>>;
  conversations(): Promise<Result<Conversation[]>>;
  openDirect(gomId: string): Promise<Result<{ id: string }>>;
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
    setNickname: (displayName) => call('PATCH', '/api/me', { displayName }),
    changePassword: (current, password) => call('PUT', '/api/me/password', { current, password }),
    disableMember: (gomId) => call('POST', `/api/users/${enc(gomId)}/disable`),
    enableMember: (gomId) => call('POST', `/api/users/${enc(gomId)}/enable`),
    friends: () => call('GET', '/api/friends'),
    askFriend: (gomId) => call('POST', '/api/friends/requests', { gomId }),
    acceptFriend: (gomId) => call('POST', `/api/friends/requests/${enc(gomId)}/accept`),
    withdrawFriend: (gomId) => call('DELETE', `/api/friends/requests/${enc(gomId)}`),
    unfriend: (gomId) => call('DELETE', `/api/friends/${enc(gomId)}`),
    blocks: () => call('GET', '/api/blocks'),
    block: (gomId) => call('POST', `/api/blocks/${enc(gomId)}`),
    unblock: (gomId) => call('DELETE', `/api/blocks/${enc(gomId)}`),
    conversations: () => call('GET', '/api/conversations'),
    openDirect: (gomId) => call('POST', '/api/conversations/direct', { gomId }),
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
