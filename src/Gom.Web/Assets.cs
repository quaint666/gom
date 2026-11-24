using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;

namespace Gom.Web;

/// <summary>
/// The fingerprinted copies of the files a page loads (main.&lt;hash&gt;.js and the like, made by `npm run build` and listed in
/// wwwroot/assets.json). A page names them instead of the plain files, so a browser may keep one for a year: a changed file is a
/// new name. Only a copy that is really what its name says is used, so a list left over from an older build is ignored (with a
/// warning) and the pages keep the plain names, which always work.
/// </summary>
public static class Assets
{
    /// <summary>The length of the hash in a name; build.mjs makes the same.</summary>
    public const int HashLength = 10;

    /// <summary>plain path → fingerprinted path, for the entries that check out. Empty when there is no list.</summary>
    public static IReadOnlyDictionary<string, string> Load(IFileProvider webRoot, ILogger log)
    {
        var list = webRoot.GetFileInfo("assets.json");
        if (!list.Exists) return new Dictionary<string, string>();
        Dictionary<string, string>? listed;
        try
        {
            using var stream = list.CreateReadStream();
            listed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        catch (JsonException) { listed = null; }
        if (listed is null)
        {
            log.LogWarning("assets.json cannot be read: pages keep the plain file names. Run npm run build.");
            return new Dictionary<string, string>();
        }

        var usable = new Dictionary<string, string>();
        foreach (var (plain, copy) in listed)
        {
            var hash = HashOf(webRoot, plain);
            if (hash is not null && copy == Named(plain, hash) && HashOf(webRoot, copy) == hash) usable[plain] = copy;
            else log.LogWarning("The fingerprinted copy of {File} is missing or out of date: pages keep the plain name. Run npm run build.", plain);
        }
        return usable;
    }

    /// <summary>The page with each plain name (in quotes, as an attribute has it) replaced by its fingerprinted one.</summary>
    public static string Apply(string html, IReadOnlyDictionary<string, string> names)
    {
        foreach (var (plain, copy) in names) html = html.Replace($"\"{plain}\"", $"\"{copy}\"", StringComparison.Ordinal);
        return html;
    }

    /// <summary>"/js/main.js" and a hash make "/js/main.&lt;hash&gt;.js".</summary>
    public static string Named(string plain, string hash)
    {
        var dot = plain.LastIndexOf('.');
        return dot <= plain.LastIndexOf('/') ? "" : $"{plain[..dot]}.{hash}{plain[dot..]}";
    }

    /// <summary>True for a name this scheme makes, which never changes under that name and may be kept for a year.</summary>
    public static bool IsFingerprinted(string fileName) =>
        System.Text.RegularExpressions.Regex.IsMatch(fileName, $@"^[A-Za-z0-9_-]+\.[0-9a-f]{{{HashLength}}}\.(js|css)$");

    private static string? HashOf(IFileProvider webRoot, string path)
    {
        var file = webRoot.GetFileInfo(path);
        if (!path.StartsWith('/') || !file.Exists) return null;
        using var stream = file.CreateReadStream();
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..HashLength];
    }
}
