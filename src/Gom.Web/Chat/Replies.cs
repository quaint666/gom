using System.Globalization;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>What a message quotes of the one it answers. With no sender and no snippet, the quoted message is not there for this reader.</summary>
public sealed record ReplyDto(Guid Id, MessageEndpoints.PersonDto? Sender, string? Snippet);

/// <summary>Works out the quote shown above a reply, for one reader.</summary>
public static class Replies
{
    /// <summary>How many characters of the quoted message are kept (as a person counts them).</summary>
    public const int SnippetChars = 100;

    /// <summary>
    /// The quote of each reply among these messages, for a reader who joined at <paramref name="readerJoinedSeq"/>. A quoted
    /// message from before they joined, or taken back, shows nothing of its words or its writer. One query, however many replies.
    /// </summary>
    public static async Task<Dictionary<Guid, ReplyDto>> For(GomDbContext db, IEnumerable<Message> messages, long readerJoinedSeq, CancellationToken ct)
    {
        var replies = messages.Where(m => m.ReplyToId is not null && m.DeletedAt is null).ToList();
        if (replies.Count == 0) return [];
        var wanted = replies.Select(m => m.ReplyToId!.Value).Distinct().ToList();
        var parents = await (from p in db.Messages.AsNoTracking() where wanted.Contains(p.Id)
                             join u in db.Users on p.SenderId equals u.Id
                             select new { p.Id, p.Seq, p.Body, p.DeletedAt, Sender = new MessageEndpoints.PersonDto(u.Id, u.DisplayName) }).ToDictionaryAsync(p => p.Id, ct);

        return replies.ToDictionary(m => m.Id, m =>
        {
            var id = m.ReplyToId!.Value;
            if (!parents.TryGetValue(id, out var p) || p.DeletedAt is not null || p.Seq <= readerJoinedSeq) return Hidden(id);
            return new ReplyDto(id, p.Sender, Snippet(p.Body));
        });
    }

    public static ReplyDto Hidden(Guid id) => new(id, null, null);

    private static string Snippet(string body)
    {
        var info = new StringInfo(body);
        return info.LengthInTextElements <= SnippetChars ? body : info.SubstringByTextElements(0, SnippetChars);
    }
}
