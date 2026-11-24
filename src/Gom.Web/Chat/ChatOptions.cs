namespace Gom.Web.Chat;

/// <summary>Bound to the "Chat" configuration section.</summary>
public sealed class ChatOptions
{
    /// <summary>The longest message, in characters as a person counts them.</summary>
    public int MaxChars { get; set; } = 4000;
    /// <summary>How many messages one person may send in a quick burst.</summary>
    public int SendBurst { get; set; } = 20;
    /// <summary>After the burst, how many more per second.</summary>
    public double SendPerSecond { get; set; } = 2;
    /// <summary>The most people in one group, the creator included.</summary>
    public int MaxGroupMembers { get; set; } = 50;
    /// <summary>Open sockets one person may hold (phone, laptop, a tab or two). The oldest is closed to make room.</summary>
    public int MaxSocketsPerUser { get; set; } = 5;
    /// <summary>Frames waiting for one socket. A client that falls this far behind is closed and catches up from its cursor.</summary>
    public int SocketQueue { get; set; } = 64;
    /// <summary>How long one frame may take to leave before the socket is given up on.</summary>
    public int SocketSendTimeoutSeconds { get; set; } = 10;
    /// <summary>Frames a socket may send that cannot be read before it is closed (1008).</summary>
    public int MaxBadFrames { get; set; } = 5;
}
