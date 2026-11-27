import { vi } from 'vitest';
import type { Api, Failure, Result } from '../Scripts/api.js';

export const ok = <T>(data: T): Result<T> => ({ ok: true, data });
export const fail = (error: Failure['error'], status = 400, retryAfter?: number): Failure =>
  retryAfter === undefined ? { ok: false, status, error } : { ok: false, status, error, retryAfter };

/** An Api whose every call answers "unauthorized" unless a test says otherwise. */
export function fakeApi(overrides: Partial<{ [K in keyof Api]: ReturnType<typeof vi.fn> }> = {}): Api & { [K in keyof Api]: ReturnType<typeof vi.fn> } {
  const nope = () => vi.fn(async () => fail('unauthorized', 401));
  const base = {
    me: nope(), signIn: nope(), signOut: vi.fn(async () => ok(undefined)), peekInvite: nope(), join: nope(), peekReset: nope(),
    reset: nope(), sessions: nope(), endSession: vi.fn(async () => ok(undefined)), createInvite: nope(), invites: nope(),
    revokeInvite: vi.fn(async () => ok(undefined)), members: nope(), storage: nope(), signUpOpen: vi.fn(async () => ok({ open: false })), signUp: nope(), withdrawSignUp: vi.fn(async () => ok(undefined)), signUpRequests: vi.fn(async () => ok({ open: false, requests: [] })), contact: vi.fn(async () => ok(undefined)), contactMessages: vi.fn(async () => ok({ messages: [] })), deleteContact: vi.fn(async () => ok(undefined)), setSignUpOpen: vi.fn(async () => ok(undefined)), approveJoin: vi.fn(async () => ok(undefined)), refuseJoin: vi.fn(async () => ok(undefined)), deleteJoin: vi.fn(async () => ok(undefined)), deleteEmptyGroup: vi.fn(async () => ok(undefined)), trimPreview: nope(), trimFiles: nope(), trimMessagesPreview: nope(), trimMessages: nope(), resetLink: nope(),
    setNickname: nope(), changePassword: vi.fn(async () => ok(undefined)), setAvatar: nope(), removeAvatar: vi.fn(async () => ok(undefined)), pushState: vi.fn(async () => fail('unknown', 404)), setPush: vi.fn(async () => ok(undefined)), removePush: vi.fn(async () => ok(undefined)), disableMember: vi.fn(async () => ok(undefined)),
    enableMember: vi.fn(async () => ok(undefined)),
    friends: nope(), askFriend: nope(), acceptFriend: vi.fn(async () => ok(undefined)), withdrawFriend: vi.fn(async () => ok(undefined)),
    unfriend: vi.fn(async () => ok(undefined)), blocks: nope(), block: vi.fn(async () => ok(undefined)), unblock: vi.fn(async () => ok(undefined)),
    conversations: nope(), openDirect: nope(), history: nope(), deleteAccount: nope(), send: nope(), sendSticker: nope(), upload: nope(), media: vi.fn(async () => ok({ items: [], hasMore: false })), copyAttachment: nope(), markRead: vi.fn(async () => ok(undefined)),
    createGroup: nope(), groupMembers: nope(), person: nope(), addMember: vi.fn(async () => ok(undefined)), leaveGroup: vi.fn(async () => ok(undefined)), setMute: vi.fn(async () => ok(undefined)), setPinned: vi.fn(async () => ok(undefined)), setMarkedUnread: vi.fn(async () => ok(undefined)), setHidden: vi.fn(async () => ok(undefined)), clearConversation: vi.fn(async () => ok(undefined)), renameGroup: vi.fn(async () => ok(undefined)), removeMember: vi.fn(async () => ok(undefined)), setJoinApproval: vi.fn(async () => ok(undefined)), joinRequests: vi.fn(async () => ok({ requests: [] })), approveRequest: vi.fn(async () => ok(undefined)), declineRequest: vi.fn(async () => ok(undefined)), bans: vi.fn(async () => ok({ bans: [] })), unban: vi.fn(async () => ok(undefined)), setRole: vi.fn(async () => ok(undefined)), handOver: vi.fn(async () => ok(undefined)), disband: vi.fn(async () => ok(undefined)), askMember: nope(), recall: vi.fn(async () => ok(undefined)), hideMessage: vi.fn(async () => ok(undefined)), search: vi.fn(async () => ok({ results: [] })), edit: vi.fn(async () => ok(undefined)), react: vi.fn(async () => ok(undefined)), unreact: vi.fn(async () => ok(undefined)), pins: vi.fn(async () => ok({ pins: [] })), pin: vi.fn(async () => ok(undefined)), unpin: vi.fn(async () => ok(undefined)),
    reads: vi.fn(async () => ok({ enabled: true, reads: [] })), setReceipts: vi.fn(async () => ok(undefined)), setLanguage: vi.fn(async () => ok(undefined)), setPalette: vi.fn(async () => ok(undefined)), setPattern: vi.fn(async () => ok(undefined)), setLook: vi.fn(async () => ok(undefined)),
    setDisappear: vi.fn(async () => ok(undefined)), twoStepNew: nope(), twoStepOn: nope(), twoStepOff: nope(),
    stickerPacks: vi.fn(async () => ok([])), createStickerPack: nope(), deleteStickerPack: vi.fn(async () => ok(undefined)), addSticker: nope(), deleteSticker: vi.fn(async () => ok(undefined)),
  };
  return { ...base, ...overrides } as never;
}

/** Waits for pending promises and a render. */
export const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

export function fill(root: ParentNode, selector: string, value: string): void {
  const input = root.querySelector<HTMLInputElement>(selector);
  if (!input) throw new Error(`No ${selector}`);
  input.value = value;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

export function submit(root: ParentNode): void {
  root.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
}

export const text = (root: ParentNode): string => root.textContent?.replace(/\s+/g, ' ').trim() ?? '';
