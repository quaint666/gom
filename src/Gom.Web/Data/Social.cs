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
    /// <summary>This person muted the conversation: it keeps its unread count but is quiet (no push, a grey badge). Theirs alone.</summary>
    public bool Muted { get; set; }
}

/// <summary>The materialized state of one message. The event stream (<see cref="MessageEvent"/>) is what clients follow.</summary>
public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    /// <summary>The conversation sequence number of the event that created it. Orders history.</summary>
    public long Seq { get; set; }
    public Guid SenderId { get; set; }
    /// <summary>Chosen by the sender's device; with the sender it makes a send safe to retry.</summary>
    public Guid ClientId { get; set; }
    /// <summary>The message this one answers, in the same conversation. Null when it answers nothing.</summary>
    public Guid? ReplyToId { get; set; }
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
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
