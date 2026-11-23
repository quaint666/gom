using System.Reflection;

namespace Gom.Web.Auth;

public enum PasswordProblem { None, TooShort, TooLong, TooCommon }

/// <summary>Length only, no composition rules: at least 10 characters, at most 128, and not a very common one.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    private static readonly Lazy<HashSet<string>> Common = new(LoadCommon);

    public static PasswordProblem Check(string password)
    {
        // Count what a person sees as characters, not UTF-16 units or bytes.
        var length = new System.Globalization.StringInfo(password).LengthInTextElements;
        if (length < MinLength) return PasswordProblem.TooShort;
        if (length > MaxLength) return PasswordProblem.TooLong;
        return Common.Value.Contains(password.ToLowerInvariant()) ? PasswordProblem.TooCommon : PasswordProblem.None;
    }

    private static HashSet<string> LoadCommon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Gom.Web.Auth.common-passwords.txt")
            ?? throw new InvalidOperationException("common-passwords.txt is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}
