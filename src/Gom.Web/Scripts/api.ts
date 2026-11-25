import type { DeviceSubscription } from './push.js';
import { language, say, type Language } from './say.js';
/** The error codes the server sends, each once. A code missing here would reach the page as 'unknown' and lose its own words. */
const SERVER_CODE_LIST = [
  'invalid_credentials', 'too_many_attempts', 'invalid_link', 'id_taken', 'invalid_gom_id', 'password_too_short',
  'password_too_long', 'password_too_common', 'invalid_display_name', 'missing_fields', 'bad_origin', 'wrong_password', 'cannot_disable_owner',
  'confirm_mismatch', 'owner_cannot_delete', 'account_deleted', 'group_not_empty', 'invalid_trim', 'trim_not_confirmed',
  'user_not_found', 'cannot_add_self', 'already_friends', 'you_blocked_them', 'not_friends', 'cannot_chat_self',
  'empty_message', 'message_too_long', 'invalid_message', 'too_many_messages', 'invalid_client_id', 'client_id_reused', 'invalid_seq',
  'invalid_title', 'no_members', 'group_too_big', 'already_member', 'group_full', 'not_a_group', 'not_your_message',
  'not_allowed', 'leader_must_hand_over', 'invalid_role', 'invalid_member', 'use_leave', 'invalid_reply', 'invalid_query',
  'pin_limit', 'invalid_reaction', 'banned', 'already_requested', 'invalid_setting',
  'invalid_invite', 'too_large', 'animated_too_large', 'image_dimensions', 'invalid_image', 'busy', 'too_many_uploads', 'daily_upload_limit', 'uploads_disabled', 'empty_upload', 'invalid_attachment', 'too_many_attachments',
  'invalid_endpoint', 'invalid_keys', 'invalid_level', 'not_editable', 'invalid_sticker', 'invalid_mute', 'invalid_kind',
  'invalid_note', 'sign_up_closed', 'sign_up_full', 'pending_approval', 'not_a_request', 'invalid_language', 'invalid_palette', 'invalid_pattern',
] as const;

/** A server's code, or what the page says when there is none: no session, refused, no network, a server fault, anything else. */
export type ErrorCode = (typeof SERVER_CODE_LIST)[number] | 'unauthorized' | 'forbidden' | 'network' | 'server' | 'unknown';

const SERVER_CODES: ReadonlySet<string> = new Set<string>(SERVER_CODE_LIST);

export interface Failure { ok: false; status: number; error: ErrorCode; retryAfter?: number }
export type Result<T> = { ok: true; data: T } | Failure;

/** `avatar` is the id of the profile picture (at /api/avatars/{avatar}); none means the initials. */
/** `standing`: a member, or someone who asked to join and is waiting ("pending") or was refused. */
export type Standing = 'member' | 'pending' | 'refused';
export interface User { id: string; gomId: string; displayName: string; isOwner: boolean; readReceipts?: boolean; uploads?: boolean; avatar?: string | null; standing?: Standing; language?: Language; palette?: string; pattern?: string }
/** Someone asking to join, as the owner sees them. */
export interface JoinRequest { gomId: string; displayName: string; note: string; requestedAt: string; refused: boolean }
export interface SignUpBody { gomId: string; password: string; displayName: string; note: string }
export interface SessionInfo { id: string; deviceLabel: string; createdAt: string; lastSeenAt: string; current: boolean }
/** A link the owner gives out. It works for `maxUses` people (any number when null) until it expires or is revoked. */
export interface Invite {
  id: string; createdAt: string; expiresAt: string; uses: number; maxUses: number | null;
  status: 'active' | 'full' | 'revoked' | 'expired'; members: { gomId: string; displayName: string; usedAt: string }[];
}
export interface Member { gomId: string; displayName: string; isOwner: boolean; disabled: boolean; deleted?: boolean; createdAt: string }
/** A count of pictures or files and the bytes they take. */
export interface Totals { count: number; bytes: number }
/** One group in the storage view; `members` 0 is a group nobody is in, which the owner may delete. */
export interface GroupUse { id: string; title: string | null; members: number; messages: number; files: number; bytes: number }
export interface PersonUse { gomId: string; displayName: string; deleted: boolean; files: number; bytes: number }
/** What Gom keeps, for the owner. One-to-one chats are counted together; disk and folder are missing when uploads are off. */
export interface Storage {
  disk: { total: number; free: number } | null; database: number; uploadsFolder: number | null; pictures: Totals; files: Totals;
  direct: { conversations: number; messages: number; files: number; bytes: number }; groups: GroupUse[]; people: PersonUse[];
}
/** What to trim: sent before `before`; `kind` 'all', 'image' or 'file'; `where` 'all', 'direct' (every one-to-one chat) or a group's id. */
export interface TrimRule { before: string; kind: 'all' | 'image' | 'file'; where: string; minBytes: number | null }
/** What a trim would remove (a preview) or removed. */
export interface TrimResult { pictures: Totals; files: Totals; conversations: number }
/** What a message trim would remove or removed: the messages people wrote, and the pictures and files they carried. */
export interface MessageTrimResult extends TrimResult { messages: number }
export interface Issued { id?: string; token: string; path: string; expiresAt: string }
export interface JoinBody { gomId: string; password: string; displayName: string }

export interface Person { gomId: string; displayName: string; avatar?: string | null }
export interface FriendRequest extends Person { createdAt: string }
export interface FriendLists { friends: Person[]; incoming: FriendRequest[]; outgoing: FriendRequest[] }
/** Who wrote a message: a name and an opaque id. The Gom ID is shown to friends only, so it is not here. */
export interface Sender { id: string; displayName: string }
/** One emoji on a message and the members who chose it. */
export interface Reaction { emoji: string; users: Sender[] }
/** The quote above an answer. With no sender and no snippet, the quoted message is not there for this reader. A quoted sticker comes as its code. */
export interface Reply { id: string; sender: Sender | null; snippet: string | null; sticker?: string | null }
/**
 * A picture or a file in a message. Fetch it at /api/attachments/{id}; a picture also has a small version at /api/attachments/{id}/thumb.
 * `removed`: the owner trimmed it; only a placeholder is shown, and it cannot be opened, saved or forwarded.
 */
export interface Attachment { id: string; kind: 'image' | 'file'; name: string; size: number; width: number | null; height: number | null; removed?: boolean; animated?: boolean }
/** A picture or file of a conversation, with the message it came in: who sent it and its words (the viewer shows them under the picture). */
export interface MediaItem { attachment: Attachment; messageId: string; seq: number; createdAt: string; sender: Sender; body: string }
/**
 * A line in a thread that says what happened (who was added or taken out, who left, a new name; or, in any conversation, that the
 * owner trimmed pictures and files, or messages, sent before `before`): not something anyone wrote.
 */
export interface Notice { type: 'added' | 'removed' | 'left' | 'renamed' | 'filesTrimmed' | 'messagesTrimmed'; target?: Sender | null; title?: string | null; before?: string | null }
export interface ChatMessage { id: string; seq: number; conversationId: string; clientId: string; sender: Sender; body: string; createdAt: string; deleted?: boolean; reply?: Reply | null; reactions?: Reaction[] | null; editedAt?: string | null; attachments?: Attachment[] | null; notice?: Notice | null;
  /** Set when the message is one sticker (its code): then it has no words and no files. */
  sticker?: string | null }
/** One step of a conversation's stream, as the server sends it (live or in a sync page). */
export interface ChatEvent { seq: number; kind: 'created' | 'deleted' | 'updated'; message: ChatMessage }
/** `attachment` is what the last message carries (its first one), so the list can say "Hình ảnh" when there are no words. */
export interface LastMessage { seq: number; body: string; createdAt: string; sender: Sender; deleted?: boolean; attachment?: 'image' | 'file' | null; sticker?: string | null }
/** How much a notification on this device says: who and where, or only "Tin nhắn mới". */
export type PushLevel = 'words' | 'name' | 'nothing';
export interface PushState { key: string; subscribed: boolean; level: PushLevel }
export interface Conversation {
  /** `peer.gomId` is there only while you are friends (the chat stays after an unfriend or a block; their ID does not). */
  id: string; kind: 'direct' | 'group'; title: string | null; peer: (Omit<Person, 'gomId'> & { gomId: string | null; id: string; deleted?: boolean }) | null; lastSeq: number; lastReadSeq: number;
  unread: number; lastMessage: LastMessage | null; memberCount?: number; joinApproval?: boolean;
  /** Muted, until turned back on or, with `mutedUntil`, until then (the list says so only while that is ahead). */
  muted?: boolean; mutedUntil?: string | null;
  /** Your own handling of it in your list: pinned to the top, and marked unread to come back to (your read position does not move). */
  pinned?: boolean; markedUnread?: boolean;
  /** Hidden by you until something new is said: listed so a search can find it, and left out of view. */
  hidden?: boolean;
}
/**
 * Someone's card, as you may see it: their Gom ID only if you are friends, where a friend request between you stands, and the groups
 * you are both in (most recently active first).
 */
export interface PersonCard {
  id: string; displayName: string; avatar?: string | null; gomId: string | null; friend: 'none' | 'sent' | 'received' | 'friends';
  commonGroups: { id: string; title: string | null; memberCount: number }[];
  /** The account was deleted: nothing can be asked of it any more. */
  deleted?: boolean;
}
/** Someone a member wants to add to a group that asks for approval, and who asked. */
export interface JoinRequestRow { id: string; displayName: string; by: Sender; createdAt: string }
/** Someone who may not be added back to a group. */
export interface BanRow { id: string; displayName: string; createdAt: string }
/** A member of a group. The Gom ID is there only for you and your friends. */
export interface GroupMember {
  id: string; displayName: string; avatar?: string | null; isMe: boolean; isFriend: boolean; gomId: string | null; request: 'none' | 'sent' | 'received';
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
  /** `days` 1 to 30 and `uses` 1 to 100 (0 for any number); without them the server's defaults. */
  createInvite(options?: { days: number; uses: number }): Promise<Result<Issued>>;
  invites(): Promise<Result<Invite[]>>;
  revokeInvite(id: string): Promise<Result<void>>;
  members(): Promise<Result<Member[]>>;
  /** Whether anyone may ask to join (the sign-in page offers it). */
  signUpOpen(): Promise<Result<{ open: boolean }>>;
  signUp(body: SignUpBody): Promise<Result<User>>;
  /** The person takes back their own request: the account goes. */
  withdrawSignUp(): Promise<Result<void>>;
  signUpRequests(): Promise<Result<{ open: boolean; requests: JoinRequest[] }>>;
  setSignUpOpen(open: boolean): Promise<Result<void>>;
  approveJoin(gomId: string): Promise<Result<void>>;
  refuseJoin(gomId: string): Promise<Result<void>>;
  deleteJoin(gomId: string): Promise<Result<void>>;
  storage(): Promise<Result<Storage>>;
  /** Deletes a group nobody is in, with everything in it. */
  deleteEmptyGroup(id: string): Promise<Result<void>>;
  trimPreview(rule: TrimRule): Promise<Result<TrimResult>>;
  /** Removes for good what the rule covers; `confirm` is what the owner typed ("Delete"). */
  trimFiles(rule: TrimRule, confirm: string): Promise<Result<TrimResult>>;
  /** Messages sent before `before`, in `where` ('all', 'direct' or a group's id). */
  trimMessagesPreview(rule: { before: string; where: string }): Promise<Result<MessageTrimResult>>;
  trimMessages(rule: { before: string; where: string }, confirm: string): Promise<Result<MessageTrimResult>>;
  resetLink(gomId: string): Promise<Result<Issued>>;
  setNickname(displayName: string): Promise<Result<User>>;
  changePassword(current: string, password: string): Promise<Result<void>>;
  /** Deletes your own account for good; `gomId` is what the person typed to confirm it. */
  deleteAccount(gomId: string): Promise<Result<void>>;
  /** Makes this picture your profile picture (the server keeps a square from its middle); the answer is its id. */
  setAvatar(picture: Blob): Promise<Result<{ avatar: string }>>;
  removeAvatar(): Promise<Result<void>>;
  /** The server's key for notifications and whether this device gets them; a failure (404) means push is off on this server. */
  pushState(): Promise<Result<PushState>>;
  setPush(subscription: DeviceSubscription, level: PushLevel): Promise<Result<void>>;
  removePush(): Promise<Result<void>>;
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
  /** The person's language, for every device and their notifications. */
  setLanguage(language: Language): Promise<Result<void>>;
  /** The colour of the person's pages, for every device. */
  setPalette(palette: string): Promise<Result<void>>;
  /** The background behind the person's messages, for every device. */
  setPattern(pattern: string): Promise<Result<void>>;
  send(conversation: string, clientId: string, body: string, replyTo?: string, attachments?: string[]): Promise<Result<ChatMessage>>;
  /** Sends one sticker, the whole message (no words, no files). */
  sendSticker(conversation: string, clientId: string, sticker: string, replyTo?: string): Promise<Result<ChatMessage>>;
  /** The pictures ("image") or files ("file") of a conversation, newest first; `before` is the seq of the last item of the page you have. */
  media(conversation: string, kind: 'image' | 'file', before?: number): Promise<Result<{ items: MediaItem[]; hasMore: boolean }>>;
  /** Copies a picture or file you may see into another conversation as a new upload of yours (not yet in a message): this is how it is forwarded. */
  copyAttachment(conversation: string, from: string): Promise<Result<Attachment>>;
  /** Sends one picture or file to a conversation (not yet in a message). `onProgress` gets 0 to 1 while the bytes go up. */
  upload(conversation: string, file: Blob & { name?: string }, onProgress: (fraction: number) => void): Promise<Result<Attachment>>;
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
  /** The card of someone you are friends with or share a conversation with; anybody else (and you) is a 404. */
  person(userId: string): Promise<Result<PersonCard>>;
  /** 204 when the person is in; 202 with status "pending" when the group asks for approval first. */
  addMember(conversation: string, gomId: string): Promise<Result<{ status: 'pending' } | undefined>>;
  leaveGroup(conversation: string): Promise<Result<void>>;
  /** Without `until`, muting lasts until it is turned back on. */
  setMute(conversation: string, muted: boolean, until?: string): Promise<Result<void>>;
  /** The "..." menu of a conversation in the list: each is yours alone. */
  setPinned(conversation: string, on: boolean): Promise<Result<void>>;
  setMarkedUnread(conversation: string, on: boolean): Promise<Result<void>>;
  /** Out of your list until something new is said in it. */
  setHidden(conversation: string, on: boolean): Promise<Result<void>>;
  /** Deletes the conversation for you: what was said so far is gone for you alone. */
  clearConversation(conversation: string): Promise<Result<void>>;
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
/**
 * How long a request may take before the page gives up on it and calls it a network failure. Without one, a request lost on a bad mobile
 * line could hang for minutes, and the messages queued behind it in a conversation with it. Giving up is safe: a message is sent again
 * with the same client id, so the server stores it once.
 */
export const REQUEST_TIMEOUT_MS = 30_000;
/** The owner's trims work through every conversation: they get longer. */
const SLOW_REQUEST_TIMEOUT_MS = 300_000;
/** An upload is given up when no byte has gone for this long (a large file on a slow line takes minutes, but it keeps moving). */
export const UPLOAD_STALL_MS = 60_000;

/**
 * What the page does when the server says something about the whole page: `onPending`, this account is a request to join still
 * waiting (show the waiting screen); `onLanguage`, the person's language is not this page's (remember it and load the page again).
 */
export interface ApiHooks { onPending?: () => void; onLanguage?: (chosen: Language) => void; onPalette?: (chosen: string) => void; onPattern?: (chosen: string) => void }

export function createApi(fetchFn: typeof fetch = (input, init) => fetch(input, init), makeXhr: () => XMLHttpRequest = () => new XMLHttpRequest(), hooks: ApiHooks = {}): Api {
  async function call<T>(method: string, path: string, body?: unknown, timeoutMs = REQUEST_TIMEOUT_MS): Promise<Result<T>> {
    let response: Response;
    try {
      response = await fetchFn(path, {
        method,
        credentials: 'same-origin',
        signal: AbortSignal.timeout(timeoutMs),
        ...(body === undefined ? {} : body instanceof Blob ? { headers: { 'Content-Type': 'application/octet-stream' }, body }
          : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
      });
    } catch {
      return { ok: false, status: 0, error: 'network' };
    }
    if (response.ok) {
      if (response.status === 204) return { ok: true, data: undefined as T };
      try { return { ok: true, data: (await response.json()) as T }; } catch { return { ok: false, status: response.status, error: 'server' }; }
    }
    const failed = await failure(response);
    if (failed.error === 'pending_approval') hooks.onPending?.();
    return failed;
  }

  /** fetch cannot tell how much of an upload has gone, so uploads use XMLHttpRequest. */
  function upload(conversation: string, file: Blob & { name?: string }, onProgress: (fraction: number) => void): Promise<Result<Attachment>> {
    return new Promise((resolve) => {
      const xhr = makeXhr();
      xhr.open('POST', `/api/conversations/${encodeURIComponent(conversation)}/attachments?name=${encodeURIComponent(file.name ?? say.chat.fileFallbackName)}`);
      xhr.withCredentials = true;
      xhr.setRequestHeader('Content-Type', 'application/octet-stream');
      // Given up when nothing has moved for a while; every bit of progress starts the wait again.
      let stall = 0;
      const watch = () => { clearTimeout(stall); stall = setTimeout(() => xhr.abort(), UPLOAD_STALL_MS) as unknown as number; };
      xhr.upload.onprogress = (e) => { watch(); if (e.lengthComputable && e.total > 0) onProgress(e.loaded / e.total); };
      xhr.onerror = xhr.ontimeout = xhr.onabort = () => { clearTimeout(stall); resolve({ ok: false, status: 0, error: 'network' }); };
      xhr.onload = () => {
        clearTimeout(stall);
        let json: unknown = null;
        try { json = JSON.parse(xhr.responseText); } catch { /* not JSON: a proxy's own answer */ }
        if (xhr.status >= 200 && xhr.status < 300 && json !== null) { resolve({ ok: true, data: json as Attachment }); return; }
        const code = typeof json === 'object' && json !== null ? (json as { error?: unknown }).error : undefined;
        const error: ErrorCode = typeof code === 'string' && SERVER_CODES.has(code) ? (code as ErrorCode)
          : xhr.status === 413 ? 'too_large' : xhr.status === 401 ? 'unauthorized' : xhr.status >= 500 ? 'server' : 'unknown';
        resolve({ ok: false, status: xhr.status, error });
      };
      xhr.send(file);
      watch();
    });
  }

  const enc = encodeURIComponent;
  return {
    upload,
    me: async () => {
      const result = await call<User>('GET', '/api/me');
      if (result.ok && result.data.language !== undefined && result.data.language !== language) hooks.onLanguage?.(result.data.language);
      if (result.ok && result.data.palette !== undefined) hooks.onPalette?.(result.data.palette);
      if (result.ok && result.data.pattern !== undefined) hooks.onPattern?.(result.data.pattern);
      return result;
    },
    signIn: (gomId, password) => call('POST', '/api/session', { gomId, password }),
    signOut: () => call('DELETE', '/api/session'),
    peekInvite: (token) => call('GET', `/api/join/${enc(token)}`),
    join: (token, body) => call('POST', `/api/join/${enc(token)}`, body),
    peekReset: (token) => call('GET', `/api/reset/${enc(token)}`),
    reset: (token, password) => call('POST', `/api/reset/${enc(token)}`, { password }),
    sessions: () => call('GET', '/api/sessions'),
    endSession: (id) => call('DELETE', `/api/sessions/${enc(id)}`),
    createInvite: (options) => call('POST', '/api/invites', options),
    invites: () => call('GET', '/api/invites'),
    revokeInvite: (id) => call('DELETE', `/api/invites/${enc(id)}`),
    members: () => call('GET', '/api/users'),
    signUpOpen: () => call('GET', '/api/sign-up'),
    signUp: (body) => call('POST', '/api/sign-up', body),
    withdrawSignUp: () => call('DELETE', '/api/sign-up'),
    signUpRequests: () => call('GET', '/api/join-requests'),
    setSignUpOpen: (open) => call('PUT', '/api/sign-up-open', { open }),
    approveJoin: (gomId) => call('POST', `/api/join-requests/${enc(gomId)}/approve`),
    refuseJoin: (gomId) => call('POST', `/api/join-requests/${enc(gomId)}/refuse`),
    deleteJoin: (gomId) => call('DELETE', `/api/join-requests/${enc(gomId)}`),
    storage: () => call('GET', '/api/owner/storage'),
    deleteEmptyGroup: (id) => call('DELETE', `/api/owner/groups/${enc(id)}`),
    trimPreview: (rule) => call('POST', '/api/owner/trim/files/preview', rule),
    trimFiles: (rule, confirm) => call('POST', '/api/owner/trim/files', { ...rule, confirm }, SLOW_REQUEST_TIMEOUT_MS),
    trimMessagesPreview: (rule) => call('POST', '/api/owner/trim/messages/preview', rule),
    trimMessages: (rule, confirm) => call('POST', '/api/owner/trim/messages', { ...rule, confirm }, SLOW_REQUEST_TIMEOUT_MS),
    resetLink: (gomId) => call('POST', `/api/users/${enc(gomId)}/reset-link`),
    setNickname: (displayName) => call('PATCH', '/api/me', { displayName }),
    changePassword: (current, password) => call('PUT', '/api/me/password', { current, password }),
    deleteAccount: (gomId) => call('DELETE', '/api/me', { gomId }),
    setAvatar: (picture) => call('PUT', '/api/me/avatar', picture),
    removeAvatar: () => call('DELETE', '/api/me/avatar'),
    pushState: () => call('GET', '/api/push'),
    setPush: (subscription, level) => call('PUT', '/api/push', { ...subscription, level }),
    removePush: () => call('DELETE', '/api/push'),
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
    send: (conversation, clientId, body, replyTo, attachments) => call('POST', `/api/conversations/${enc(conversation)}/messages`, { clientId, body, replyTo, ...(attachments && attachments.length > 0 ? { attachments } : {}) }),
    sendSticker: (conversation, clientId, sticker, replyTo) => call('POST', `/api/conversations/${enc(conversation)}/messages`, { clientId, sticker, replyTo }),
    createGroup: (title, members) => call('POST', '/api/conversations/group', { title, members }),
    groupMembers: (conversation) => call('GET', `/api/conversations/${enc(conversation)}/members`),
    person: (userId) => call('GET', `/api/people/${enc(userId)}`),
    addMember: (conversation, gomId) => call('POST', `/api/conversations/${enc(conversation)}/members`, { gomId }),
    setMute: (conversation, muted, until) => call('PUT', `/api/conversations/${enc(conversation)}/mute`, until === undefined ? { muted } : { muted, until }),
    setPinned: (conversation, on) => call('PUT', `/api/conversations/${enc(conversation)}/pinned`, { on }),
    setMarkedUnread: (conversation, on) => call('PUT', `/api/conversations/${enc(conversation)}/unread`, { on }),
    setHidden: (conversation, on) => call('PUT', `/api/conversations/${enc(conversation)}/hidden`, { on }),
    clearConversation: (conversation) => call('POST', `/api/conversations/${enc(conversation)}/clear`),
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
    setLanguage: (chosen) => call('PUT', '/api/me/language', { language: chosen }),
    setPalette: (chosen) => call('PUT', '/api/me/palette', { palette: chosen }),
    setPattern: (chosen) => call('PUT', '/api/me/pattern', { pattern: chosen }),
    recall: (conversation, message) => call('DELETE', `/api/conversations/${enc(conversation)}/messages/${enc(message)}`),
    hideMessage: (conversation, message) => call('PUT', `/api/conversations/${enc(conversation)}/messages/${enc(message)}/hidden`),
    media: (conversation, kind, before) => call('GET', `/api/conversations/${enc(conversation)}/media?kind=${kind}${before === undefined ? '' : `&before=${before}`}`),
    copyAttachment: (conversation, from) => call('POST', `/api/conversations/${enc(conversation)}/attachments/copy`, { from }),
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
