using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>
/// Which people a message mentions. A mention is chosen by the sender from the group's members (the page sends their ids) and written
/// in the words as "@" and the name the member has now, so it reads the same wherever it is copied and no Gom ID is shown. The server
/// keeps only the mentions that are real: a current member of this group, not the sender, and written in the words (at the start,
/// or after a space or punctuation, and not run into another word), so nobody is mentioned without it showing. "@all" needs no id.
/// Only in a group: a one-to-one chat has nobody to single out.
/// </summary>
public static class Mentions
{
    /// <summary>The word that mentions everyone in a group. Reserved: no one may take it as their Gom ID.</summary>
    public const string All = "all";

    /// <summary>The most ids one message may name (a group is at most this many).</summary>
    private const int MostAsked = 100;

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Whether the words have "@name" in them, as written for a mention: not run into the word before ("a@b.com" is not a mention of
    /// "b") or after it ("@Maia" is not a mention of "Mai"), capitals ignored. "@ name" and "@.name" are not mentions.
    /// </summary>
    public static bool Has(string body, string name)
    {
        if (name.Length == 0) return false;
        var needle = "@" + name;
        for (var at = body.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0; at = body.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + needle.Length;
            if ((at == 0 || !IsWord(body[at - 1])) && (end == body.Length || !IsWord(body[end]))) return true;
        }
        return false;
    }

    /// <summary>What a message in this conversation mentions: the members named (that are real) and whether it says "@all".</summary>
    public static async Task<(Guid[] People, bool Everyone)> Resolve(GomDbContext db, Guid conversation, ConversationKind kind, Guid sender,
        string body, IEnumerable<Guid>? asked, CancellationToken ct)
    {
        if (kind != ConversationKind.Group) return ([], false);
        var everyone = Has(body, All);
        var wanted = (asked ?? []).Where(id => id != sender).Distinct().Take(MostAsked).ToList();
        if (wanted.Count == 0) return ([], everyone);
        var members = await (from m in db.Members.AsNoTracking()
                             where m.ConversationId == conversation && wanted.Contains(m.UserId)
                             join u in db.Users on m.UserId equals u.Id
                             where u.DisabledAt == null && u.DeletedAt == null
                             select new { u.Id, u.DisplayName }).ToListAsync(ct);
        return (members.Where(p => Has(body, p.DisplayName)).Select(p => p.Id).ToArray(), everyone);
    }
}
