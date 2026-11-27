/** The limits the server holds, as every page carries them (`<body data-limits>`, written by the server's Pages.Limits). */
export interface Limits {
  /** The longest message, in characters as a person counts them. */
  messageChars: number;
  /** Pictures and files in one message. */
  attachments: number;
  imageBytes: number;
  fileBytes: number;
  /** A moving picture (GIF), kept as sent. */
  animatedBytes: number;
  /** A video, kept as sent (after the sender's browser made it smaller), and its still picture. */
  videoBytes: number;
  posterBytes: number;
  /** What one person may upload in any 24 hours, as stored. 0: no limit. */
  uploadDailyBytes: number;
  /** Pinned messages in one conversation. */
  pins: number;
  /** A display name or a group's name. */
  nameChars: number;
  passwordMin: number;
  passwordMax: number;
  gomIdMin: number;
  gomIdMax: number;
  /** Words searched for in one conversation. */
  searchChars: number;
  /** How much of an answered message its quote keeps. */
  quoteChars: number;
  /** How much of the last message the conversation list keeps. */
  listPreviewChars: number;
  /** What someone asking to join says about who they are. */
  noteChars: number;
  inviteDays: number;
  inviteUses: number;
  /** What a new invite starts at on the owner's page: the server's defaults. */
  inviteDefaultDays: number;
  inviteDefaultUses: number;
  /** The pages' code number (Pages.PageVersion), sent with every request so a server that has moved past it can say "reload". */
  pageVersion: number;
  /** The privacy page's form: the message, the way to reach the writer, and how many days the owner keeps it. */
  contactChars: number;
  contactReplyChars: number;
  contactKeepDays: number;
  /** Disappearing messages: the lengths a conversation may choose, in seconds. */
  disappearChoices: number[];
}

/** Read from the page each time (it is small), so nothing here can drift from what the server sent. */
export function limits(): Limits {
  const raw = document.body.dataset['limits'];
  if (!raw) throw new Error('This page has no data-limits: it was not served by Gom.');
  return JSON.parse(raw) as Limits;
}
