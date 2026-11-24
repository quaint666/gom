using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>The pictures and files carried by messages, in the order their senders chose. One query, however many messages.</summary>
public static class MessageAttachments
{
    public static async Task<Dictionary<Guid, IReadOnlyList<AttachmentEndpoints.AttachmentDto>>> For(GomDbContext db, IEnumerable<Guid> messageIds, CancellationToken ct)
    {
        var ids = messageIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.Attachments.AsNoTracking().Where(a => a.MessageId != null && ids.Contains(a.MessageId.Value))
            .OrderBy(a => a.Position).ThenBy(a => a.CreatedAt).ToListAsync(ct);
        return rows.GroupBy(a => a.MessageId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<AttachmentEndpoints.AttachmentDto>)g.Select(AttachmentEndpoints.ToDto).ToList());
    }
}
