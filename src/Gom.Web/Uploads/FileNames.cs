using System.Globalization;
using System.Text;

namespace Gom.Web.Uploads;

/// <summary>Makes the name a person gave a file safe to store, show and offer for saving.</summary>
public static class FileNames
{
    public const int MaxLength = 120;

    public static string Clean(string? given, string fallback = "tệp")
    {
        var name = given ?? "";
        // Only the last part: a path from the sender's computer is never kept.
        var cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) name = name[(cut + 1)..];

        var kept = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            // Controls, and the invisible marks that turn text around ("photo‮gpj.exe" would show as "photoexe.jpg").
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse) continue;
            kept.Append(rune.ToString());
        }

        var clean = kept.ToString().Trim().Trim('.').Trim();
        if (clean == "") return fallback;
        if (new StringInfo(clean).LengthInTextElements > MaxLength)
        {
            // Cut by what a person sees as a letter, and keep the end, where the type is.
            var elements = StringInfo.GetTextElementEnumerator(clean);
            var all = new List<string>();
            while (elements.MoveNext()) all.Add((string)elements.Current);
            var dot = clean.LastIndexOf('.');
            var ext = dot > 0 && clean.Length - dot <= 12 ? clean[dot..] : "";
            var room = MaxLength - new StringInfo(ext).LengthInTextElements;
            clean = string.Concat(all.Take(room)) + ext;
        }
        return clean;
    }

    /// <summary>The name of a picture after it was written again as WebP: the old name without its type, then ".webp".</summary>
    public static string AsWebp(string clean)
    {
        var dot = clean.LastIndexOf('.');
        var stem = dot > 0 ? clean[..dot] : clean;
        return stem + ".webp";
    }
}
