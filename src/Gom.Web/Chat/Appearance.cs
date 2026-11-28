namespace Gom.Web.Chat;

/// <summary>
/// The colours a person can choose for their pages. A code names a block in the stylesheet (`Styles/00-tokens.css`; <c>:root[data-palette="sea"]</c>; the default has
/// none: it is the :root accent), and Scripts/theme.ts lists the same codes in the same order; a test keeps the three together.
/// </summary>
public static class Appearance
{
    public const string DefaultPalette = "sage";

    public static readonly IReadOnlyList<string> Palettes =
        ["sage", "sea", "lotus", "turmeric", "lavender", "graphite", "jade", "indigo", "plum", "lacquer", "coffee", "moss", "clay", "sky", "wine", "rice",
         "sunset", "nightsea", "forest", "blossom", "ember", "candy", "aurora", "lime"];

    public const string DefaultPattern = "plain";

    /// <summary>
    /// The backgrounds behind the messages. "plain" has none; each other is wwwroot/patterns/{code}.svg, drawn in the colour's own
    /// pattern tone (<c>--pat</c>) through a mask, and named in both string tables; a test keeps them together.
    /// </summary>
    public static readonly IReadOnlyList<string> Patterns = ["plain", "dots", "tiles", "waves", "bamboo", "clouds", "grid", "weave", "lotus",
        "nonla", "drum", "blossom", "lantern", "koi", "stars"];

    /// <summary>The longest code, for the column.</summary>
    public const int CodeMaxLength = 16;
}
