using System.Collections.Frozen;

namespace Gom.Web.Chat;

/// <summary>
/// The stickers a message may be: still Noto Emoji pictures (Google, SIL Open Font License 1.1), named by their code points, kept
/// with the page under wwwroot/stickers. The page lists the same ones, in the same packs and order; a test keeps the two in step.
/// </summary>
public static class Stickers
{
    public static readonly FrozenSet<string> Allowed = new[]
    {
        // Cảm xúc
        "1f600", "1f604", "1f606", "1f605", "1f602", "1f923", "1f60a", "1f607", "1f609", "1f60d", "1f970", "1f618",
        "1f929", "1f60b", "1f61c", "1f92a", "1f917", "1f92d", "1f914", "1f644", "1f60f", "1f60c", "1f634", "1f971",
        "1f60e", "1f973", "1f97a", "1f622", "1f62d", "1f631", "1f633", "1f62c", "1f910", "1f621", "1f624", "1f92f",
        "1f974", "1f975", "1f976", "1f912",
        // Cử chỉ
        "1f44d", "1f44e", "1f44c", "270c", "1f91e", "1f91f", "1f919", "1f44f", "1f64c", "1f64f", "1f44b", "1f4aa",
        "1f91d", "1faf6",
        // Yêu thương
        "2764", "1f495", "1f496", "1f497", "1f493", "1f498", "1f49d", "1f494", "1f48c", "1f48b", "1f339", "1f490",
        // Động vật
        "1f415", "1f436", "1f408", "1f431", "1f43c", "1f430", "1f43b", "1f437", "1f414", "1f986", "1f41f", "1f422",
        "1f418", "1f98b",
        // Đồ ăn
        "1f35c", "1f35a", "1f356", "1f357", "1f354", "1f355", "1f370", "1f369", "1f36d", "1f9cb", "2615", "1f37b",
        "1f349", "1f96d", "1f34a",
        // Lễ hội
        "1f389", "1f38a", "1f388", "1f382", "1f381", "1f9e7", "1f9e8", "1f3ee", "1f386", "1f525", "1f31f", "2728",
        "1f3c6", "1f340", "1f4af",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The column's size: room for a longer code later (a sequence such as a skin tone) without a migration.</summary>
    public const int MaxCodeLength = 32;
}
