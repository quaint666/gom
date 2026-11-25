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
public enum NoticeKind { Added = 1, Removed = 2, Left = 3, Renamed = 4, FilesTrimmed = 5, MessagesTrimmed = 6, Restyled = 7 }

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
