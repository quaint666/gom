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
    /// <summary>How often a socket is sent a small "still here" message, in seconds. Anything in the path that cuts idle connections sees traffic, and the page can tell a dead connection from a quiet one.</summary>
    public int HeartbeatSeconds { get; set; } = 25;
    /// <summary>How many "sync" requests (a device asking for what it missed) one socket may make in a quick burst. A catch-up page is 200 events, so this is plenty.</summary>
    public int SyncBurst { get; set; } = 10;
    /// <summary>After the burst, how many more per second. A request over the limit is answered with "slow_down" and the page asks again a moment later.</summary>
    public double SyncPerSecond { get; set; } = 2;
    /// <summary>Requests turned away in a row before the socket is closed (1013); a request that is let through starts the count again. A page that waits as told never gets near this.</summary>
    public int MaxSlowSyncs { get; set; } = 5;
    /// <summary>How many messages may be pinned in one conversation.</summary>
    public int MaxPins { get; set; } = 3;
    /// <summary>How many conversations one person may pin to the top of their list.</summary>
    public int MaxPinnedConversations { get; set; } = 5;
}
