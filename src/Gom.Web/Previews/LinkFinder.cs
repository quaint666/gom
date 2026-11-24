using System.Text.RegularExpressions;

namespace Gom.Web.Previews;

/// <summary>Finds the first web address in a message, the way the page draws links: http, https or www, without the sentence's punctuation.</summary>
public static partial class LinkFinder
{
    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex Candidate();

    private static readonly char[] Trailing = ['.', ',', ';', ':', '!', '?', '\'', '"', '”', '’', '»', '…'];

    public static Uri? First(string body)
    {
        try
        {
            foreach (Match m in Candidate().Matches(body))
            {
                if (m.Index > 0 && char.IsLetterOrDigit(body[m.Index - 1]) && !m.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                var text = Trim(m.Value);
                var full = text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? text : "https://" + text;
                if (Uri.TryCreate(full, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Contains('.') && uri.AbsoluteUri.Length <= 2048)
                    return uri;
            }
        }
        catch (RegexMatchTimeoutException) { }
        return null;
    }

    private static string Trim(string text)
    {
        for (;;)
        {
            var before = text;
            text = text.TrimEnd(Trailing);
            foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
                if (text.EndsWith(close) && text.Count(c => c == close) > text.Count(c => c == open)) text = text[..^1];
            if (text == before) return text;
        }
    }
}
