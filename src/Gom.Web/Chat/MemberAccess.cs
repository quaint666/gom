using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>What a person may see of a conversation they are in.</summary>
/// <param name="Kind">One-to-one or group.</param>
/// <param name="JoinedSeq">They see only messages after this number: what was said before they joined is not theirs to read.</param>
public sealed record Access(ConversationKind Kind, long JoinedSeq)
{
    /// <summary>Null when this person is not in the conversation: the same answer as "there is no such conversation".</summary>
    public static Task<Access?> Of(GomDbContext db, Guid conversation, Guid person, CancellationToken ct) =>
        (from m in db.Members
         where m.ConversationId == conversation && m.UserId == person
         join c in db.Conversations on m.ConversationId equals c.Id
         select new Access(c.Kind, m.JoinedSeq)).SingleOrDefaultAsync(ct);
}
