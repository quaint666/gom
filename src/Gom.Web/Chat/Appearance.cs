namespace Gom.Web.Chat;

/// <summary>
/// The colours a person can choose for their pages. A code names a block in gom.css (<c>:root[data-palette="sea"]</c>; the default has
/// none: it is the :root accent), and Scripts/theme.ts lists the same codes in the same order; a test keeps the three together.
/// </summary>
public static class Appearance
{
    public const string DefaultPalette = "sage";

    public static readonly IReadOnlyList<string> Palettes =
        ["sage", "sea", "lotus", "turmeric", "lavender", "graphite", "jade", "indigo", "plum", "lacquer", "coffee", "moss"];

    /// <summary>The longest code, for the column.</summary>
    public const int CodeMaxLength = 16;
}
