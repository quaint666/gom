using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>What a person may see of a conversation they are in.</summary>
/// <param name="Kind">One-to-one or group.</param>
/// <param name="SeesAfter">They see only messages after this number: what was said before they joined, or before they deleted the
/// conversation for themself, is not theirs to read.</param>
/// <param name="Role">Plain member, deputy or leader (only a group has the last two).</param>
public sealed record Access(ConversationKind Kind, long SeesAfter, GroupRole Role)
{
    /// <summary>Null when this person is not in the conversation: the same answer as "there is no such conversation".</summary>
    public static Task<Access?> Of(GomDbContext db, Guid conversation, Guid person, CancellationToken ct) =>
        (from m in db.Members
         where m.ConversationId == conversation && m.UserId == person
         join c in db.Conversations on m.ConversationId equals c.Id
         select new Access(c.Kind, m.JoinedSeq > m.ClearedSeq ? m.JoinedSeq : m.ClearedSeq, m.Role)).SingleOrDefaultAsync(ct);
}
