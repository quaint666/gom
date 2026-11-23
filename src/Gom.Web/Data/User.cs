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

/// <summary>A single-use link: an invite (no target) or a password reset (for one existing user).</summary>
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
}
