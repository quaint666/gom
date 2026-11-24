using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>One emoji on one message and the members who chose it, in the order they did.</summary>
public sealed record ReactionDto(string Emoji, IReadOnlyList<MessageEndpoints.PersonDto> Users);

/// <summary>Reactions: a short fixed set, one per person per message.</summary>
public static class Reactions
{
    public static readonly IReadOnlyList<string> Allowed = ["👍", "❤️", "😆", "😮", "😢", "😡"];

    /// <summary>
    /// The reactions of these messages of one conversation. Only people still in the conversation are shown: someone who left or was
    /// removed takes their reaction with them. One query, however many messages.
    /// </summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<ReactionDto>>> For(GomDbContext db, Guid conversation, IEnumerable<Guid> messageIds, CancellationToken ct)
    {
        var ids = messageIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await (from r in db.Reactions.AsNoTracking() where ids.Contains(r.MessageId)
                          join m in db.Members on r.UserId equals m.UserId where m.ConversationId == conversation
                          join u in db.Users on r.UserId equals u.Id
                          orderby r.CreatedAt, u.Id
                          select new { r.MessageId, r.Emoji, UserId = u.Id, u.DisplayName }).ToListAsync(ct);
        return rows.GroupBy(r => r.MessageId).ToDictionary(g => g.Key, g => (IReadOnlyList<ReactionDto>)g.GroupBy(r => r.Emoji)
            .Select(e => new ReactionDto(e.Key, e.Select(r => new MessageEndpoints.PersonDto(r.UserId, r.DisplayName)).ToList())).ToList());
    }
}
