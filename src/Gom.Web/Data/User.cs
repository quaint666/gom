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
    /// <summary>The language of the pages and of the notifications this person gets: "vi" or "en".</summary>
    public string Language { get; set; } = ServerText.DefaultLanguage;
    /// <summary>The colour of their pages, on every device (a code from <see cref="Chat.Appearance.Palettes"/>).</summary>
    public string Palette { get; set; } = Chat.Appearance.DefaultPalette;
    /// <summary>
    /// Set while this account is a request to join (signed up on its own, waiting for the owner); null for a member. A request can
    /// sign in but do nothing else until the owner approves it.
    /// </summary>
    public DateTimeOffset? RequestedAt { get; set; }
    /// <summary>When the owner said no to the request (it stays, so the person is told and the owner can change their mind).</summary>
    public DateTimeOffset? RefusedAt { get; set; }
    /// <summary>What the person said about who they are, for the owner.</summary>
    public string? RequestNote { get; set; }
}

/// <summary>The site's own switches, changed by the owner while it runs: one row.</summary>
public class SiteSettings
{
    public int Id { get; set; } = 1;
    /// <summary>Whether anyone may ask to join (sign up and wait for approval). Invites work either way.</summary>
    public bool OpenSignUp { get; set; }
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
