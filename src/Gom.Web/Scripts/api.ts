export type ErrorCode =
  | 'invalid_credentials' | 'too_many_attempts' | 'invalid_link' | 'id_taken' | 'invalid_gom_id' | 'password_too_short'
  | 'password_too_long' | 'password_too_common' | 'invalid_display_name' | 'missing_fields' | 'bad_origin'
  | 'wrong_password' | 'cannot_disable_owner' | 'user_not_found' | 'cannot_add_self' | 'already_friends' | 'you_blocked_them'
  | 'not_friends' | 'cannot_chat_self' | 'empty_message' | 'message_too_long' | 'invalid_message' | 'too_many_messages'
  | 'invalid_client_id' | 'client_id_reused' | 'invalid_seq' | 'invalid_title' | 'no_members' | 'group_too_big' | 'already_member'
  | 'group_full' | 'not_a_group' | 'not_your_message' | 'not_allowed' | 'leader_must_hand_over' | 'invalid_role' | 'invalid_member' | 'use_leave' | 'invalid_reply' | 'invalid_query'
  | 'group_full' | 'not_a_group' | 'not_your_message' | 'not_allowed' | 'leader_must_hand_over' | 'invalid_role' | 'invalid_member' | 'use_leave' | 'invalid_reply' | 'pin_limit' | 'invalid_reaction' | 'banned' | 'already_requested' | 'invalid_setting'
  | 'unauthorized' | 'forbidden' | 'network' | 'server' | 'unknown';

const SERVER_CODES: ReadonlySet<string> = new Set<ErrorCode>([
  'invalid_credentials', 'too_many_attempts', 'invalid_link', 'id_taken', 'invalid_gom_id', 'password_too_short',
  'password_too_long', 'password_too_common', 'invalid_display_name', 'missing_fields', 'bad_origin', 'wrong_password', 'cannot_disable_owner',
  'user_not_found', 'cannot_add_self', 'already_friends', 'you_blocked_them', 'not_friends', 'cannot_chat_self',
  'empty_message', 'message_too_long', 'invalid_message', 'too_many_messages', 'invalid_client_id', 'client_id_reused', 'invalid_seq',
  'invalid_title', 'no_members', 'group_too_big', 'already_member', 'group_full', 'not_a_group', 'not_your_message',
  'not_allowed', 'leader_must_hand_over', 'invalid_role', 'invalid_member', 'use_leave', 'invalid_reply', 'invalid_query',
  'not_allowed', 'leader_must_hand_over', 'invalid_role', 'invalid_member', 'use_leave', 'invalid_reply', 'pin_limit', 'invalid_reaction', 'banned', 'already_requested', 'invalid_setting',
]);

export interface Failure { ok: false; status: number; error: ErrorCode; retryAfter?: number }
export type Result<T> = { ok: true; data: T } | Failure;

export interface User { id: string; gomId: string; displayName: string; isOwner: boolean; readReceipts?: boolean }
export interface SessionInfo { id: string; deviceLabel: string; createdAt: string; lastSeenAt: string; current: boolean }
export interface Invite { id: string; createdAt: string; expiresAt: string; usedBy: string | null; status: 'pending' | 'used' | 'revoked' | 'expired' }
export interface Member { gomId: string; displayName: string; isOwner: boolean; disabled: boolean; createdAt: string }
export interface Issued { id?: string; token: string; path: string; expiresAt: string }
export interface JoinBody { gomId: string; password: string; displayName: string }

export interface Person { gomId: string; displayName: string }
export interface FriendRequest extends Person { createdAt: string }
export interface FriendLists { friends: Person[]; incoming: FriendRequest[]; outgoing: FriendRequest[] }
/** Who wrote a message: a name and an opaque id. The Gom ID is shown to friends only, so it is not here. */
export interface Sender { id: string; displayName: string }
/** One emoji on a message and the members who chose it. */
export interface Reaction { emoji: string; users: Sender[] }
/** The quote above an answer. With no sender and no snippet, the quoted message is not there for this reader. */
export interface Reply { id: string; sender: Sender | null; snippet: string | null }
export interface ChatMessage { id: string; seq: number; conversationId: string; clientId: string; sender: Sender; body: string; createdAt: string; deleted?: boolean; reply?: Reply | null; reactions?: Reaction[] | null; editedAt?: string | null }
/** One step of a conversation's stream, as the server sends it (live or in a sync page). */
export interface ChatEvent { seq: number; kind: 'created' | 'deleted' | 'updated'; message: ChatMessage }
export interface LastMessage { seq: number; body: string; createdAt: string; sender: Sender; deleted?: boolean }
export interface Conversation {
  id: string; kind: 'direct' | 'group'; title: string | null; peer: Person | null; lastSeq: number; lastReadSeq: number;
  unread: number; lastMessage: LastMessage | null; memberCount?: number; muted?: boolean; joinApproval?: boolean;
}
/** Someone a member wants to add to a group that asks for approval, and who asked. */
export interface JoinRequestRow { id: string; displayName: string; by: Sender; createdAt: string }
/** Someone who may not be added back to a group. */
export interface BanRow { id: string; displayName: string; createdAt: string }
/** A member of a group. The Gom ID is there only for you and your friends. */
export interface GroupMember {
  id: string; displayName: string; isMe: boolean; isFriend: boolean; gomId: string | null; request: 'none' | 'sent' | 'received';
  role: 'leader' | 'deputy' | 'member';
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
  history(conversation: string, before?: number): Promise<Result<{ messages: ChatMessage[]; hasMore: boolean; lastSeq: number }>>;
  recall(conversation: string, message: string): Promise<Result<void>>;
  /** How far the other members have read. Empty, with `enabled: false`, when you have hidden yours. */
  reads(conversation: string): Promise<Result<{ enabled: boolean; reads: { userId: string; seq: number }[] }>>;
  setReceipts(enabled: boolean): Promise<Result<void>>;
  send(conversation: string, clientId: string, body: string, replyTo?: string): Promise<Result<ChatMessage>>;
  markRead(conversation: string, seq: number): Promise<Result<void>>;
  edit(conversation: string, message: string, body: string): Promise<Result<void>>;
  react(conversation: string, message: string, emoji: string): Promise<Result<void>>;
  unreact(conversation: string, message: string): Promise<Result<void>>;
  search(conversation: string, query: string): Promise<Result<{ results: ChatMessage[] }>>;
  pins(conversation: string): Promise<Result<{ pins: ChatMessage[] }>>;
  pin(conversation: string, message: string): Promise<Result<void>>;
  unpin(conversation: string, message: string): Promise<Result<void>>;
  hideMessage(conversation: string, message: string): Promise<Result<void>>;
  createGroup(title: string, members: string[]): Promise<Result<{ id: string }>>;
  groupMembers(conversation: string): Promise<Result<GroupMember[]>>;
  /** 204 when the person is in; 202 with status "pending" when the group asks for approval first. */
  addMember(conversation: string, gomId: string): Promise<Result<{ status: 'pending' } | undefined>>;
  leaveGroup(conversation: string): Promise<Result<void>>;
  setMute(conversation: string, muted: boolean): Promise<Result<void>>;
  renameGroup(conversation: string, title: string): Promise<Result<void>>;
  removeMember(conversation: string, userId: string, ban?: boolean): Promise<Result<void>>;
  setJoinApproval(conversation: string, on: boolean): Promise<Result<void>>;
  joinRequests(conversation: string): Promise<Result<{ requests: JoinRequestRow[] }>>;
  approveRequest(conversation: string, userId: string): Promise<Result<void>>;
  declineRequest(conversation: string, userId: string): Promise<Result<void>>;
  bans(conversation: string): Promise<Result<{ bans: BanRow[] }>>;
  unban(conversation: string, userId: string): Promise<Result<void>>;
  setRole(conversation: string, userId: string, role: 'deputy' | 'member'): Promise<Result<void>>;
  handOver(conversation: string, userId: string): Promise<Result<void>>;
  disband(conversation: string): Promise<Result<void>>;
  askMember(conversation: string, userId: string): Promise<Result<{ status: 'pending' | 'friends' }>>;
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
    history: (conversation, before) => call('GET', `/api/conversations/${enc(conversation)}/messages${before === undefined ? '' : `?before=${before}`}`),
    send: (conversation, clientId, body, replyTo) => call('POST', `/api/conversations/${enc(conversation)}/messages`, { clientId, body, replyTo }),
    createGroup: (title, members) => call('POST', '/api/conversations/group', { title, members }),
    groupMembers: (conversation) => call('GET', `/api/conversations/${enc(conversation)}/members`),
    addMember: (conversation, gomId) => call('POST', `/api/conversations/${enc(conversation)}/members`, { gomId }),
    setMute: (conversation, muted) => call('PUT', `/api/conversations/${enc(conversation)}/mute`, { muted }),
    renameGroup: (conversation, title) => call('PATCH', `/api/conversations/${enc(conversation)}`, { title }),
    leaveGroup: (conversation) => call('DELETE', `/api/conversations/${enc(conversation)}/members/me`),
    removeMember: (conversation, userId, ban) => call('DELETE', `/api/conversations/${enc(conversation)}/members/${enc(userId)}${ban ? '?ban=true' : ''}`),
    setJoinApproval: (conversation, on) => call('PUT', `/api/conversations/${enc(conversation)}/settings`, { joinApproval: on }),
    joinRequests: (conversation) => call('GET', `/api/conversations/${enc(conversation)}/requests`),
    approveRequest: (conversation, userId) => call('POST', `/api/conversations/${enc(conversation)}/requests/${enc(userId)}/approve`),
    declineRequest: (conversation, userId) => call('DELETE', `/api/conversations/${enc(conversation)}/requests/${enc(userId)}`),
    bans: (conversation) => call('GET', `/api/conversations/${enc(conversation)}/bans`),
    unban: (conversation, userId) => call('DELETE', `/api/conversations/${enc(conversation)}/bans/${enc(userId)}`),
    setRole: (conversation, userId, role) => call('PUT', `/api/conversations/${enc(conversation)}/members/${enc(userId)}/role`, { role }),
    handOver: (conversation, userId) => call('POST', `/api/conversations/${enc(conversation)}/leader`, { userId }),
    disband: (conversation) => call('DELETE', `/api/conversations/${enc(conversation)}`),
    askMember: (conversation, userId) => call('POST', `/api/conversations/${enc(conversation)}/members/${enc(userId)}/friend-request`),
    reads: (conversation) => call('GET', `/api/conversations/${enc(conversation)}/reads`),
    setReceipts: (enabled) => call('PATCH', '/api/me/receipts', { enabled }),
    recall: (conversation, message) => call('DELETE', `/api/conversations/${enc(conversation)}/messages/${enc(message)}`),
    hideMessage: (conversation, message) => call('PUT', `/api/conversations/${enc(conversation)}/messages/${enc(message)}/hidden`),
    search: (conversation, query) => call('GET', `/api/conversations/${enc(conversation)}/search?q=${enc(query)}`),
    pins: (conversation) => call('GET', `/api/conversations/${enc(conversation)}/pins`),
    pin: (conversation, message) => call('PUT', `/api/conversations/${enc(conversation)}/messages/${enc(message)}/pin`),
    unpin: (conversation, message) => call('DELETE', `/api/conversations/${enc(conversation)}/messages/${enc(message)}/pin`),
    react: (conversation, message, emoji) => call('PUT', `/api/conversations/${enc(conversation)}/messages/${enc(message)}/reaction`, { emoji }),
    unreact: (conversation, message) => call('DELETE', `/api/conversations/${enc(conversation)}/messages/${enc(message)}/reaction`),
    edit: (conversation, message, body) => call('PATCH', `/api/conversations/${enc(conversation)}/messages/${enc(message)}`, { body }),
    markRead: (conversation, seq) => call('POST', `/api/conversations/${enc(conversation)}/read`, { seq }),
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
