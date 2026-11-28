using Gom.Web.Auth;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Social;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Chat;

/// <summary>
/// Disappearing messages (H): a conversation may have what is said in it deleted for good a while after it was sent. In a one-to-one
/// chat either person sets it; in a group, the leader and the deputies. A line in the conversation says who changed it and to what.
/// Each message carries its own deadline from the moment it was sent (<see cref="Message.ExpiresAt"/>); a job deletes those that are
/// due, as the owner's trim does (<see cref="MessageTrim.DeleteMessages"/>), and the pages take them away at their deadline themselves.
/// Notices never disappear. What it cannot reach: notifications already shown on a phone, screenshots, copies forwarded elsewhere.
/// </summary>
public static class Disappearing
{
    public sealed record SettingRequest(int? Seconds);

    /// <summary>The lengths offered (H): 15 and 30 minutes, 1, 3, 6 and 12 hours, a day, a week. The page gets them in data-limits.</summary>
    public static readonly int[] Choices = [15 * 60, 30 * 60, 3600, 3 * 3600, 6 * 3600, 12 * 3600, 86_400, 7 * 86_400];

    /// <summary>How often the job looks for messages that are due.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    public static void MapDisappearing(this IEndpointRouteBuilder app) =>
        app.MapPut("/api/conversations/{id:guid}/disappear", Set).RequireAuthorization();

    private static async Task<IResult> Set(Guid id, SettingRequest request, HttpContext http, GomDbContext db, LiveDelivery live, TimeProvider clock, CancellationToken ct)
    {
        if (request.Seconds is { } s && !Choices.Contains(s)) return Results.BadRequest(new { error = "invalid_setting" });
        var me = GomClaims.User(http.User);
        var access = await Access.Of(db, id, me, ct);
        if (access is null) return Results.NotFound();
        if (access.Kind == ConversationKind.Group && access.Role < GroupRole.Deputy) return Results.Json(new { error = "not_allowed" }, statusCode: StatusCodes.Status403Forbidden);
        if (access.Kind == ConversationKind.Direct && !await MessageEndpoints.StillFriends(db, id, me, ct))
            return Results.Json(new { error = "not_friends" }, statusCode: StatusCodes.Status403Forbidden);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await GroupEndpoints.LockGroup(db, id, ct);   // the same lock a send takes: a message is sent wholly before or after the change
        if (!await db.Members.AnyAsync(m => m.ConversationId == id && m.UserId == me, ct)) return Results.NotFound();
        var before = await db.Conversations.Where(c => c.Id == id).Select(c => c.DisappearSeconds).SingleAsync(ct);
        if (before == request.Seconds) return Results.NoContent();
        await db.Conversations.Where(c => c.Id == id).ExecuteUpdateAsync(x => x.SetProperty(c => c.DisappearSeconds, request.Seconds), ct);
        var notice = await Notices.Write(db, id, me, NoticeKind.Disappearing, null,
            (request.Seconds ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture), clock.GetUtcNow(), ct);
        var everyone = await db.Members.Where(m => m.ConversationId == id).Select(m => m.UserId).ToListAsync(ct);
        await tx.CommitAsync(ct);
        live.ConversationsChanged(everyone);
        await Notices.Publish(db, live, notice, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Deletes the messages that are due, a conversation at a time, locked as a send locks it; their files' bytes go after each commit.
    /// The people in each conversation get their lists again (the last message and the unread count may have changed). Returns how many.
    /// </summary>
    public static async Task<int> RunOnce(GomDbContext db, IBlobStore store, LiveDelivery live, DateTimeOffset now, CancellationToken ct)
    {
        var conversations = await db.Messages.Where(m => m.ExpiresAt != null && m.ExpiresAt <= now).Select(m => m.ConversationId).Distinct().Take(100).ToListAsync(ct);
        var total = 0;
        foreach (var conversation in conversations)
        {
            List<MessageTrim.GoneFile> files;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await GroupEndpoints.LockGroup(db, conversation, ct);
                // At most a batch, so the conversation is not locked for long; the next sweep takes the rest.
                var ids = await db.Messages.Where(m => m.ConversationId == conversation && m.ExpiresAt != null && m.ExpiresAt <= now)
                    .OrderBy(m => m.Seq).Select(m => m.Id).Take(MessageTrim.Batch).ToListAsync(ct);
                if (ids.Count == 0) continue;
                files = await MessageTrim.DeleteMessages(db, ids, ct);
                await tx.CommitAsync(ct);
                total += ids.Count;
            }
            AttachmentFiles.Forget(store, [.. files.Select(f => f.Id)]);
            live.ConversationsChanged(await db.Members.Where(m => m.ConversationId == conversation).Select(m => m.UserId).ToListAsync(ct));
        }
        return total;
    }
}

public sealed class DisappearingJanitor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DisappearingJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Disappearing.Every);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var p = scope.ServiceProvider;
                    await Disappearing.RunOnce(p.GetRequiredService<GomDbContext>(), p.GetRequiredService<IBlobStore>(), p.GetRequiredService<LiveDelivery>(), clock.GetUtcNow(), stop);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Deleting disappearing messages failed; it will be tried again"); }
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }
}
