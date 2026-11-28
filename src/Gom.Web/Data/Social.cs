namespace Gom.Web.Data;

public enum FriendStatus { Pending, Accepted }

/// <summary>
/// One row per pair of people. The pair is stored in a fixed order (<see cref="UserLow"/> sorts before <see cref="UserHigh"/>)
/// so "A asks B" and "B asks A" can never be two rows.
/// </summary>
public class Friendship
{
    public Guid UserLow { get; set; }
    public Guid UserHigh { get; set; }
    /// <summary>Who sent the request: one of the two.</summary>
    public Guid RequestedBy { get; set; }
    public FriendStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>Puts a pair in the stored order. Postgres compares uuids byte by byte, which is the same as comparing their lowercase hex text.</summary>
    public static (Guid Low, Guid High) Order(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);
}

/// <summary>The blocker no longer hears from the blocked person: no friend request, no one-to-one message.</summary>
public class Block
{
    public Guid BlockerId { get; set; }
    public Guid BlockedId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum ConversationKind { Direct, Group }

public class Conversation
{
    public Guid Id { get; set; }
    public ConversationKind Kind { get; set; }
    /// <summary>For a one-to-one conversation, "low:high" of the two user ids, so a pair has at most one. Null for a group.</summary>
    public string? DirectKey { get; set; }
    /// <summary>A group's name. Null for a one-to-one conversation (the other person's name is shown).</summary>
    public string? Title { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>The sequence number of the newest message; bumped in the same transaction as the insert.</summary>
    public long LastSeq { get; set; }
    /// <summary>When the newest message arrived; null before the first. Orders the list without a scan.</summary>
    public DateTimeOffset? LastMessageAt { get; set; }
    /// <summary>On: someone adding a person to a group needs the leader or a deputy to approve it (the leader and deputies add directly).</summary>
    public bool JoinApproval { get; set; }
    /// <summary>
    /// The conversation's own colour and background, the same for everyone in it (codes from <see cref="Chat.Appearance"/>); null: each
    /// person sees their own.
    /// </summary>
    public string? Palette { get; set; }
    public string? Pattern { get; set; }
    /// <summary>
    /// Disappearing messages: how long a message sent now lasts, in seconds (one of <see cref="Chat.Disappearing.Choices"/>); null: for
    /// good. Each message keeps the deadline it was sent with (<see cref="Message.ExpiresAt"/>), so a change counts from then on.
    /// </summary>
    public int? DisappearSeconds { get; set; }
    /// <summary>A group's picture (its file is in the blob store under this id, as a profile picture's is); null: its initials.</summary>
    public Guid? AvatarId { get; set; }
}

/// <summary>What a member may do in a group. A one-to-one chat has only plain members.</summary>
public enum GroupRole { Member = 0, Deputy = 1, Leader = 2 }

public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    /// <summary>How far this member has read: one number per member, however many messages it covers.</summary>
    public long LastReadSeq { get; set; }
    /// <summary>The conversation's counter when this member joined: they see only what comes after. Zero for a one-to-one chat.</summary>
    public long JoinedSeq { get; set; }
    /// <summary>The creator of a group is its leader (one per group); the leader names deputies. See docs/decisions.md.</summary>
    public GroupRole Role { get; set; }
    /// <summary>This person muted the conversation until they turn it back on: it keeps its unread count but is quiet (no push, a grey badge). Theirs alone.</summary>
    public bool Muted { get; set; }
    /// <summary>Muted for a while instead: quiet until this time, then loud again by itself. Never set together with <see cref="Muted"/>.</summary>
    public DateTimeOffset? MutedUntil { get; set; }
    /// <summary>Pinned to the top of this person's list, the latest pin first. Theirs alone.</summary>
    public DateTimeOffset? PinnedAt { get; set; }
    /// <summary>This person marked it unread to come back to it. Their read position does not move (others would see it go back); reading clears it.</summary>
    public bool MarkedUnread { get; set; }
    /// <summary>Hidden from this person's list until something newer than this number is said. Theirs alone.</summary>
    public long? HiddenSeq { get; set; }
    /// <summary>This person deleted the conversation for themself up to here: like <see cref="JoinedSeq"/>, they see only what comes after.</summary>
    public long ClearedSeq { get; set; }
}

/// <summary>The materialized state of one message. The event stream (<see cref="MessageEvent"/>) is what clients follow.</summary>
/// <summary>
/// What a notice in a thread says happened. A notice is a line in the conversation, not something a person wrote.
/// <see cref="FilesTrimmed"/> and <see cref="MessagesTrimmed"/>: the owner trimmed pictures and files, or messages, sent before a date
/// (the date is the notice's body).
/// </summary>
public enum NoticeKind { Added = 1, Removed = 2, Left = 3, Renamed = 4, FilesTrimmed = 5, MessagesTrimmed = 6, Restyled = 7, Call = 8, Disappearing = 9, Pictured = 10 }

public enum CallState { Ringing = 1, Active = 2, Ended = 3 }

/// <summary>
/// How a call ended. <see cref="Missed"/>: nobody answered in time, or the caller hung up first. <see cref="Busy"/>: the person called
/// was in another call (a missed call for them). <see cref="Failed"/>: it could not go on (the server lost what was needed to set it
/// up, or the two stopped being friends while it rang).
/// </summary>
public enum CallOutcome { Completed = 1, Missed = 2, Declined = 3, Busy = 4, Failed = 5 }

/// <summary>
/// A one-to-one voice call between friends: who, when, how long, how it ended (docs/voice-calls.md, decision 13). Never the audio,
/// and never what the phones said to set it up (their addresses): that stays in memory (<see cref="Chat.CallRelay"/>).
/// </summary>
public class Call
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid CallerId { get; set; }
    public Guid CalleeId { get; set; }
    /// <summary>The caller's device: only it takes part.</summary>
    public Guid CallerSessionId { get; set; }
    /// <summary>Chosen by the caller's device, so a retried start is the same call.</summary>
    public Guid ClientId { get; set; }
    /// <summary>The call protocol the caller spoke (<see cref="Chat.CallEndpoints.ProtocolVersion"/>).</summary>
    public int Version { get; set; }
    public CallState State { get; set; }
    public CallOutcome? Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Which of the callee's devices answered: the only one of theirs that takes part.</summary>
    public Guid? AnswerSessionId { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    /// <summary>When each side last said it is still there, during the call; long silence ends it.</summary>
    public DateTimeOffset? CallerSeenAt { get; set; }
    public DateTimeOffset? CalleeSeenAt { get; set; }
    /// <summary>The line in the chat, written when the call ends.</summary>
    public Guid? MessageId { get; set; }
}

/// <summary>Someone in a call that has not ended: one row per person, so nobody is in two calls at once (the key decides, atomically).</summary>
public class CallParty
{
    public Guid UserId { get; set; }
    public Guid CallId { get; set; }
}

public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    /// <summary>The conversation sequence number of the event that created it. Orders history.</summary>
    public long Seq { get; set; }
    public Guid SenderId { get; set; }
    /// <summary>Chosen by the sender's device; with the sender it makes a send safe to retry.</summary>
    public Guid ClientId { get; set; }
    /// <summary>
    /// The message this one answers, in the same conversation. Null when it answers nothing. Not a database key: the owner may trim
    /// the answered message and keep the answer, which then shows that the quoted message is no longer there.
    /// </summary>
    public Guid? ReplyToId { get; set; }
    public string Body { get; set; } = "";
    /// <summary>Set when the message is one sticker (its code, from <see cref="Chat.Stickers"/>); then it has no words and no files.</summary>
    public string? Sticker { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>When its words were last changed by the sender; null if never. The old words are not kept.</summary>
    public DateTimeOffset? EditedAt { get; set; }
    /// <summary>Set when this is a notice ("Bảo added Mai to the group"), not a message: <see cref="SenderId"/> is who did it.</summary>
    public NoticeKind? Notice { get; set; }
    /// <summary>The person a notice is about (who was added or removed).</summary>
    public Guid? NoticeTargetId { get; set; }
    /// <summary>When it is deleted for good (sent in a conversation with disappearing messages on); null: kept.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

public enum EventKind { Created, Deleted, Updated }

/// <summary>One step in a conversation's ordered stream. A device that was away asks for "everything after seq N".</summary>
public class MessageEvent
{
    public Guid ConversationId { get; set; }
    public long Seq { get; set; }
    public EventKind Kind { get; set; }
    public Guid MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One person's own "delete for me": the message stays for everyone else. It is hidden from this person's history, sync and list.</summary>
public class MessageHide
{
    public Guid UserId { get; set; }
    public Guid MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A message pinned to the top of a conversation, for everyone in it. At most a few per conversation.</summary>
public class MessagePin
{
    public Guid ConversationId { get; set; }
    public Guid MessageId { get; set; }
    public Guid PinnedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One person's reaction to one message: at most one each, changed by choosing another.</summary>
public class MessageReaction
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public string Emoji { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A member's wish to add one of their friends to a group that asks for approval, waiting for the leader or a deputy.</summary>
public class JoinRequest
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Someone the leader or a deputy removed and does not want back: nobody can add them until the leader lifts it.</summary>
public class GroupBan
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public Guid BannedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A pack of stickers the owner made (<see cref="Chat.StickerPacks"/>); shown after Gom's own in everyone's picker until deleted.</summary>
public class StickerPack
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Taken out of the picker; stickers of it already sent still show.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>
    /// In every conversation's picker (true), or only for the people (<see cref="StickerPackPerson"/>) and in the conversations
    /// (<see cref="StickerPackChat"/>) the owner chose.
    /// </summary>
    public bool Everywhere { get; set; } = true;
}

/// <summary>Someone the owner chose for a pack that is not everywhere: they have it in every conversation they are in.</summary>
public class StickerPackPerson
{
    public Guid PackId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>A conversation the owner chose for a pack that is not everywhere: everyone in it has the pack there.</summary>
public class StickerPackChat
{
    public Guid PackId { get; set; }
    public Guid ConversationId { get; set; }
}

/// <summary>One of the owner's stickers: its picture is in the blob store under its id, as kept (a WebP, or a moving GIF or WebP as sent).</summary>
public class CustomSticker
{
    public Guid Id { get; set; }
    public Guid PackId { get; set; }
    /// <summary>Its place in the pack: the order the owner added them.</summary>
    public int Position { get; set; }
    public string ContentType { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Taken out of the picker; one already sent still shows.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
