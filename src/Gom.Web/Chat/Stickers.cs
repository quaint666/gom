using System.Collections.Frozen;

namespace Gom.Web.Chat;

/// <summary>
/// The stickers a message may be: still Noto Emoji pictures (Google, SIL Open Font License 1.1), named by their code points, kept
/// with the page under wwwroot/stickers. The page lists the same ones in the order it shows them; a test keeps the two in step.
/// </summary>
public static class Stickers
{
    public static readonly FrozenSet<string> Allowed = new[]
    {
        "1f600", "1f604", "1f606", "1f605", "1f602", "1f923", "1f60a", "1f607", "1f609", "1f60d", "1f970", "1f618",
        "1f60b", "1f61c", "1f92a", "1f917", "1f92d", "1f914", "1f644", "1f60f", "1f634", "1f60e", "1f973", "1f97a",
        "1f622", "1f62d", "1f631", "1f633", "1f621", "1f624", "1f92f", "1f912", "1f44d", "1f44f", "1f64f", "1f44b",
        "1f4aa", "1f91d", "2764", "1f494", "1f525", "1f389", "1f382", "1f381", "1f339", "1f4af", "1f415", "1f98b",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The column's size: room for a longer code later (a sequence such as a skin tone) without a migration.</summary>
    public const int MaxCodeLength = 32;
}
