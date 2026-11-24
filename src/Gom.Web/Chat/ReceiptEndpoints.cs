using Gom.Web.Auth;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>
/// Read receipts: one switch per person that works both ways. If you hide how far you have read, nobody sees yours, and you do not see theirs.
/// The position itself is always kept (it drives your own unread count); the switch only decides who may be shown it.
/// </summary>
public static class ReceiptEndpoints
{
    public sealed record SwitchRequest(bool? Enabled);

    public static void MapReceipts(this IEndpointRouteBuilder app)
    {
        app.MapPatch("/api/me/receipts", Switch).RequireAuthorization();
        app.MapGet("/api/conversations/{id:guid}/reads", Reads).RequireAuthorization();
    }

    private static async Task<IResult> Switch(SwitchRequest request, HttpContext http, GomDbContext db, LiveDelivery live, CancellationToken ct)
    {
        if (request.Enabled is null) return Results.BadRequest(new { error = "missing_fields" });
        var me = GomClaims.User(http.User);
        var enabled = request.Enabled.Value;
        await db.Users.Where(u => u.Id == me).ExecuteUpdateAsync(s => s.SetProperty(u => u.ShareReadReceipts, enabled), ct);

        // Everyone I talk to should look again at what they may see of me, and my other devices at what I may see of them.
        var mine = db.Members.Where(m => m.UserId == me).Select(m => m.ConversationId);
        var people = await db.Members.Where(m => mine.Contains(m.ConversationId)).Select(m => m.UserId).Distinct().ToListAsync(ct);
        live.ConversationsChanged(people);
        return Results.NoContent();
    }

    private static async Task<IResult> Reads(Guid id, HttpContext http, GomDbContext db, CancellationToken ct)
    {
        var me = GomClaims.User(http.User);
        if (await Access.Of(db, id, me, ct) is null) return Results.NotFound();

        var iShare = await db.Users.Where(u => u.Id == me).Select(u => u.ShareReadReceipts).SingleAsync(ct);
        if (!iShare) return Results.Ok(new { enabled = false, reads = Array.Empty<object>() });

        var reads = await (from m in db.Members where m.ConversationId == id && m.UserId != me
                           join u in db.Users on m.UserId equals u.Id where u.ShareReadReceipts
                           select new { userId = m.UserId, seq = m.LastReadSeq }).ToListAsync(ct);
        return Results.Ok(new { enabled = true, reads });
    }
}
