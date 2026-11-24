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
}

public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    /// <summary>How far this member has read: one number per member, however many messages it covers.</summary>
    public long LastReadSeq { get; set; }
}
