using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Gom.Web.Previews;

/// <summary>What a page says about itself: its title, a description, the name of its site and a picture.</summary>
public sealed record PageMeta(string? Title, string? Description, string? Site, string? Image);

/// <summary>Reads the head of a page. It is not an HTML parser: it looks at the meta tags and the title only, and trusts nothing it finds.</summary>
public static partial class PageMetaReader
{
    [GeneratedRegex(@"<meta\s[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetaTag();

    [GeneratedRegex("""([a-zA-Z:_-]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+))""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TitleTag();

    public static PageMeta Read(byte[] html, Uri page)
    {
        try
        {
            var text = Encoding.UTF8.GetString(html);
            // Whatever follows the head is not wanted, and it keeps the search short.
            var end = text.IndexOf("</head", StringComparison.OrdinalIgnoreCase);
            if (end > 0) text = text[..end];
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match tag in MetaTag().Matches(text))
            {
                string? key = null, value = null;
                foreach (Match a in Attribute().Matches(tag.Value))
                {
                    var name = a.Groups[1].Value.ToLowerInvariant();
                    var v = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                    if (name is "property" or "name") key ??= v.ToLowerInvariant();
                    else if (name == "content") value = v;
                }
                if (key is not null && value is not null) meta.TryAdd(key, value);
            }
            var title = Pick(meta.GetValueOrDefault("og:title"), meta.GetValueOrDefault("twitter:title"), TitleTag().Match(text) is { Success: true } t ? t.Groups[1].Value : null);
            var description = Pick(meta.GetValueOrDefault("og:description"), meta.GetValueOrDefault("twitter:description"), meta.GetValueOrDefault("description"));
            var site = Pick(meta.GetValueOrDefault("og:site_name"));
            var image = Pick(meta.GetValueOrDefault("og:image"), meta.GetValueOrDefault("og:image:url"), meta.GetValueOrDefault("twitter:image"));
            return new PageMeta(Limit(title, 200), Limit(description, 300), Limit(site, 60) ?? Limit(page.Host, 60), Absolute(image, page));
        }
        catch (RegexMatchTimeoutException) { return new PageMeta(null, null, page.Host, null); }
    }

    private static string? Pick(params string?[] candidates) => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    /// <summary>Plain text for a small label: entities decoded, runs of space made one, control characters dropped, cut to length by letters.</summary>
    private static string? Limit(string? raw, int max)
    {
        if (raw is null) return null;
        var decoded = WebUtility.HtmlDecode(raw);
        var clean = new StringBuilder();
        var space = false;
        foreach (var rune in decoded.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune)) { space = clean.Length > 0; continue; }
            if (space) { clean.Append(' '); space = false; }
            clean.Append(rune.ToString());
        }
        var text = clean.ToString();
        if (text.Length == 0) return null;
        if (text.Length <= max) return text;
        var cut = text[..max];
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut + "…";
    }

    /// <summary>The picture's address in full, and only when it is http or https.</summary>
    private static string? Absolute(string? image, Uri page)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var decoded = WebUtility.HtmlDecode(image.Trim());
        return Uri.TryCreate(page, decoded, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;
    }
}
