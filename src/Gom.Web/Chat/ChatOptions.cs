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
}
