using System.Globalization;

namespace Gom.Web.Auth;

/// <summary>A nickname, as in Zalo: up to 60 characters, no control or invisible formatting characters. Empty means "use the Gom ID".</summary>
public static class DisplayName
{
    public const int MaxLength = 60;

    /// <summary>The cleaned name, the fallback when empty, or null when it is not allowed.</summary>
    public static string? Normalize(string? input, string fallback)
    {
        var name = input?.Trim();
        if (string.IsNullOrEmpty(name)) return fallback;
        if (new StringInfo(name).LengthInTextElements > MaxLength) return null;
        foreach (var c in name)
        {
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return null;
        }
        return name;
    }
}
