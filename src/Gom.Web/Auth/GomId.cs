using System.Text.RegularExpressions;

namespace Gom.Web.Auth;

/// <summary>The rules for a Gom ID: lowercase letters, digits and underscore, 3 to 20 characters, not reserved.</summary>
public static partial class GomId
{
    public const string DatabasePattern = "^[a-z0-9_]{3,20}$";

    // Names that could pass for the service itself or for staff.
    private static readonly HashSet<string> Reserved =
        ["admin", "administrator", "gom", "ops", "root", "staff", "support", "system", "owner", "bot", "help", "null"];

    public static bool IsValid(string? id) =>
        id is not null && Pattern().IsMatch(id) && !Reserved.Contains(id);

    [GeneratedRegex(DatabasePattern, RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();
}
