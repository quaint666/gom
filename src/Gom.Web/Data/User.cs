namespace Gom.Web.Data;

public class User
{
    public Guid Id { get; set; }
    /// <summary>Always lowercase; the database refuses anything else.</summary>
    public string GomId { get; set; } = "";
    /// <summary>A nickname; starts out equal to the Gom ID.</summary>
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsOwner { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    /// <summary>When the account was deleted (it is then disabled too, and emptied); and by whom: so far always the person themself.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    /// <summary>Whether others may see how far this person has read, and whether this person may see theirs. One switch, both ways.</summary>
    public bool ShareReadReceipts { get; set; } = true;
    /// <summary>
    /// The profile picture: the name of its file in the blob store, a new one each time it changes (so its address can be cached for
    /// good). Null shows the initials.
    /// </summary>
    public Guid? AvatarId { get; set; }
}

public class Session
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>SHA-256 of the cookie token; the token itself is never stored.</summary>
    public byte[] TokenHash { get; set; } = [];
    /// <summary>A coarse label such as "Chrome on Android"; not the raw User-Agent and not the IP.</summary>
    public string DeviceLabel { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public enum LinkKind { Invite, Reset }

/// <summary>A link: an invite (no target; it may let in several people) or a password reset (for one existing user, used once).</summary>
public class OneTimeLink
{
    public Guid Id { get; set; }
    public LinkKind Kind { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public Guid CreatedBy { get; set; }
    public Guid? TargetUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public Guid? ConsumedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>For an invite: how many people may join through it (null: any number, until it expires or is revoked).</summary>
    public int? MaxUses { get; set; }
    /// <summary>For an invite: how many have joined through it. Taken in one atomic step, so a link with 3 uses left is never used a 4th time.</summary>
    public int Uses { get; set; }
}

/// <summary>Who joined through which invite, and when.</summary>
public class InviteUse
{
    public Guid LinkId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset UsedAt { get; set; }
}
