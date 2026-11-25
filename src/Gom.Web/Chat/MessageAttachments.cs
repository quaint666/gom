using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>What rides along with a message: its pictures and files, and the notice it stands for.</summary>
public sealed record MessageExtras(IReadOnlyList<AttachmentEndpoints.AttachmentDto>? Attachments, NoticeDto? Notice = null);

public static class MessageAttachments
{
    /// <summary>The extras of these messages in two queries, however many messages. Messages with none are not in the result.</summary>
    public static async Task<Dictionary<Guid, MessageExtras>> For(GomDbContext db, IEnumerable<Guid> messageIds, CancellationToken ct)
    {
        var ids = messageIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var files = (await db.Attachments.AsNoTracking().Where(a => a.MessageId != null && ids.Contains(a.MessageId.Value))
            .OrderBy(a => a.Position).ThenBy(a => a.CreatedAt).ToListAsync(ct))
            .GroupBy(a => a.MessageId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<AttachmentEndpoints.AttachmentDto>)g.Select(AttachmentEndpoints.ToDto).ToList());
        var notices = await Notices.For(db, ids, ct);
        var result = new Dictionary<Guid, MessageExtras>();
        foreach (var id in files.Keys.Concat(notices.Keys).Distinct())
        {
            result[id] = new MessageExtras(files.GetValueOrDefault(id), notices.GetValueOrDefault(id));
        }
        return result;
    }
}
