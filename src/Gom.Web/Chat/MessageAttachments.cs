using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>What a link preview shows. The picture is fetched like any other, at /api/attachments/{imageId}/thumb.</summary>
public sealed record PreviewDto(string Url, string? Title, string? Description, string? Site, Guid? ImageId);

/// <summary>What rides along with a message: its pictures and files, and the preview of its address.</summary>
public sealed record MessageExtras(IReadOnlyList<AttachmentEndpoints.AttachmentDto>? Attachments, PreviewDto? Preview, NoticeDto? Notice = null);

public static class MessageAttachments
{
    /// <summary>The extras of these messages in two queries, however many messages. Messages with none are not in the result.</summary>
    public static async Task<Dictionary<Guid, MessageExtras>> For(GomDbContext db, IEnumerable<Guid> messageIds, CancellationToken ct)
    {
        var ids = messageIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var files = (await db.Attachments.AsNoTracking().Where(a => a.MessageId != null && a.Kind != AttachmentKind.Preview && ids.Contains(a.MessageId.Value))
            .OrderBy(a => a.Position).ThenBy(a => a.CreatedAt).ToListAsync(ct))
            .GroupBy(a => a.MessageId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<AttachmentEndpoints.AttachmentDto>)g.Select(AttachmentEndpoints.ToDto).ToList());
        var previews = await db.Previews.AsNoTracking().Where(p => ids.Contains(p.MessageId)).ToDictionaryAsync(p => p.MessageId, ct);
        var notices = await Notices.For(db, ids, ct);
        var result = new Dictionary<Guid, MessageExtras>();
        foreach (var id in files.Keys.Concat(previews.Keys).Concat(notices.Keys).Distinct())
        {
            var p = previews.GetValueOrDefault(id);
            result[id] = new MessageExtras(files.GetValueOrDefault(id), p is null ? null : new PreviewDto(p.Url, p.Title, p.Description, p.Site, p.ImageId), notices.GetValueOrDefault(id));
        }
        return result;
    }
}
