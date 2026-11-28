using System.Text.RegularExpressions;

namespace Gom.Web.Auth;

/// <summary>The rules for a Gom ID: lowercase letters, digits and underscore, 3 to 20 characters, not reserved.</summary>
public static partial class GomId
{
    // The lengths are written once, as text, because the pattern below must be a constant and C# cannot put a number in one.
    private const string Shortest = "3", Longest = "20";
    public const string DatabasePattern = "^[a-z0-9_]{" + Shortest + "," + Longest + "}$";
    public static readonly int MinLength = int.Parse(Shortest, System.Globalization.CultureInfo.InvariantCulture);
    public static readonly int MaxLength = int.Parse(Longest, System.Globalization.CultureInfo.InvariantCulture);

    // Names that could pass for the service itself or for staff.
    private static readonly HashSet<string> Reserved =
        ["admin", "administrator", "all", "gom", "ops", "root", "staff", "support", "system", "owner", "bot", "help", "null"];

    public static bool IsValid(string? id) =>
        id is not null && Pattern().IsMatch(id) && !Reserved.Contains(id);

    [GeneratedRegex(DatabasePattern, RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();
}
