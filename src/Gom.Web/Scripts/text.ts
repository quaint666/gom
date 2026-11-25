import type { Failure } from './api.js';
import { limits } from './limits.js';
import { say } from './say.js';

/** A number as the page's language writes it: 4.000 and 1,2 in Vietnamese. */
const number = (n: number): string => new Intl.NumberFormat(say.locale, { maximumFractionDigits: 1 }).format(n);

/** The sentence for a failure, from the table. The sign-in sentence never says whether the ID exists. */
export function errorText(failure: Failure): string {
  const l = limits();
  switch (failure.error) {
    case 'invalid_credentials': return say.errors.invalidCredentials;
    case 'too_many_attempts':
      return failure.retryAfter === undefined ? say.errors.tooManyAttempts : say.errors.tooManyAttemptsWait(failure.retryAfter);
    case 'id_taken': return say.errors.idTaken;
    case 'invalid_gom_id': return say.errors.invalidGomId(l.gomIdMin, l.gomIdMax);
    case 'password_too_short': return say.errors.passwordTooShort(l.passwordMin);
    case 'password_too_long': return say.errors.passwordTooLong(l.passwordMax);
    case 'password_too_common': return say.errors.passwordTooCommon;
    case 'invalid_display_name': return say.errors.invalidDisplayName(l.nameChars);
    case 'wrong_password': return say.errors.wrongPassword;
    case 'cannot_disable_owner': return say.errors.cannotDisableOwner;
    case 'confirm_mismatch': return say.errors.confirmMismatch;
    case 'owner_cannot_delete': return say.errors.ownerCannotDelete;
    case 'account_deleted': return say.errors.accountDeleted;
    case 'group_not_empty': return say.errors.groupNotEmpty;
    case 'invalid_trim': return say.errors.invalidTrim;
    case 'trim_not_confirmed': return say.errors.trimNotConfirmed;
    case 'user_not_found': return say.errors.userNotFound;
    case 'cannot_add_self': return say.errors.cannotAddSelf;
    case 'already_friends': return say.errors.alreadyFriends;
    case 'you_blocked_them': return say.errors.youBlockedThem;
    case 'not_friends': return say.errors.notFriends;
    case 'cannot_chat_self': return say.errors.cannotChatSelf;
    case 'empty_message': return say.errors.emptyMessage;
    case 'message_too_long': return say.errors.messageTooLong(number(l.messageChars));
    case 'invalid_message': return say.errors.invalidMessage;
    case 'too_many_messages': return say.errors.tooManyMessages;
    case 'client_id_reused': case 'invalid_client_id': case 'invalid_seq': return say.errors.clientIdReused;
    case 'invalid_title': return say.errors.invalidTitle(l.nameChars);
    case 'no_members': return say.errors.noMembers;
    case 'group_too_big': case 'group_full': return say.errors.groupTooBig;
    case 'already_member': return say.errors.alreadyMember;
    case 'not_a_group': return say.errors.notAGroup;
    case 'not_allowed': return say.errors.notAllowed;
    case 'leader_must_hand_over': return say.errors.leaderMustHandOver;
    case 'invalid_role': case 'invalid_member': return say.errors.invalidRole;
    case 'invalid_reply': return say.errors.invalidReply;
    case 'invalid_query': return say.errors.invalidQuery(l.searchChars);
    case 'pin_limit': return say.errors.pinLimit(l.pins);
    case 'invalid_reaction': return say.errors.invalidReaction;
    case 'banned': return say.errors.banned;
    case 'already_requested': return say.errors.alreadyRequested;
    case 'invalid_setting': return say.errors.invalidSetting;
    case 'use_leave': return say.errors.useLeave;
    case 'not_your_message': return say.errors.notYourMessage;
    case 'missing_fields': return say.errors.missingFields;
    case 'invalid_invite': return say.errors.invalidInvite(l.inviteDays, l.inviteUses);
    case 'too_large': return say.errors.tooLarge(formatSize(l.imageBytes), formatSize(l.fileBytes));
    case 'animated_too_large': return say.errors.animatedTooLarge(formatSize(l.animatedBytes));
    case 'image_dimensions': return say.errors.imageDimensions;
    case 'invalid_image': return say.errors.invalidImage;
    case 'busy': return say.errors.busy;
    case 'too_many_uploads': return say.errors.tooManyUploads;
    case 'daily_upload_limit': return say.errors.dailyUploadLimit(formatSize(l.uploadDailyBytes));
    case 'uploads_disabled': return say.errors.uploadsDisabled;
    case 'empty_upload': return say.errors.emptyUpload;
    case 'invalid_attachment': return say.errors.invalidAttachment;
    case 'too_many_attachments': return tooManyAttachments(l.attachments);
    case 'invalid_link': return say.errors.invalidLink;
    case 'bad_origin': return say.errors.badOrigin;
    case 'invalid_level': return say.errors.invalidLevel;
    case 'not_editable': return say.errors.notEditable;
    case 'invalid_sticker': return say.errors.invalidSticker;
    case 'invalid_mute': return say.errors.invalidMute;
    case 'invalid_kind': return say.errors.invalidKind;
    case 'invalid_note': return say.errors.invalidNote(l.noteChars);
    case 'sign_up_closed': return say.errors.signUpClosed;
    case 'sign_up_full': return say.errors.signUpFull;
    case 'pending_approval': return say.errors.pendingApproval;
    case 'not_a_request': return say.errors.notARequest;
    case 'invalid_language': return say.errors.invalidLanguage;
    case 'invalid_endpoint': return say.errors.invalidEndpoint;
    case 'invalid_keys': return say.errors.invalidKeys;
    case 'network': return say.errors.network;
    case 'server': return say.errors.server;
    default: return say.errors.other;
  }
}

/** Said when a message would carry more pictures and files than the server takes, by the server or by the page before it asks. */
export const tooManyAttachments = (most: number): string => say.errors.tooManyAttachments(most);

/** The hint under a new password. */
export const passwordHint = (): string => say.common.passwordHint(limits().passwordMin);

/** At most `chars` characters as a person counts them (a flag or a family emoji is one), with "…" when cut. */
export function clip(text: string, chars: number): string {
  const parts = [...new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(text)].map((s) => s.segment);
  return parts.length > chars ? parts.slice(0, chars).join('') + '…' : text;
}

/** A size in words: "1,2 MB". */
export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${number(bytes / 1024)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${number(bytes / (1024 * 1024))} MB`;
  return `${number(bytes / (1024 * 1024 * 1024))} GB`;
}
