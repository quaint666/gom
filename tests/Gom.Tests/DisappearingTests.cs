using System.Net;
using System.Text.Json;
using Gom.Web.Chat;
using Gom.Web.Chat.Live;
using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gom.Tests;

/// <summary>Disappearing messages: who may set it, the deadline each message gets when sent, and the job that deletes them.</summary>
public class DisappearingTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Put = HttpMethod.Put, Get = HttpMethod.Get, Post = HttpMethod.Post;

    [Fact]
    public void The_lengths_offered_are_H_s_eight_and_the_page_gets_them()
    {
        Assert.Equal([900, 1800, 3600, 10800, 21600, 43200, 86400, 604800], Disappearing.Choices);
        Assert.Contains("\"disappearChoices\":[900,1800,3600,10800,21600,43200,86400,604800]", Gom.Web.Pages.Limits(new(), new(), new(), new()), StringComparison.Ordinal);
    }

    private async Task<(User A, string CA, User B, string CB, Guid Chat)> Pair()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return (a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private async Task<(string Leader, string Deputy, string Member, Guid Group)> Group()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà mình", members = new[] { b.GomId, c.GomId } }, ca);
        var group = (await AppFixture.Json(r)).GetProperty("id").GetGuid();
        using var role = await app.Call(Put, $"/api/conversations/{group}/members/{b.Id}/role", new { role = "deputy" }, ca);
        Assert.Equal(HttpStatusCode.NoContent, role.StatusCode);
        return (ca, cb, cc, group);
    }

    private Task<HttpResponseMessage> Set(Guid chat, string cookie, int? seconds) =>
        app.Call(Put, $"/api/conversations/{chat}/disappear", new { seconds }, cookie);

    private async Task<JsonElement> Send(Guid chat, string cookie, string body)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private async Task<JsonElement> Listed(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat).Clone();
    }

    private async Task<List<JsonElement>> History(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/messages", cookie: cookie);
        return [.. (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().Select(m => m.Clone())];
    }

    private static string? Error(JsonElement json) => json.GetProperty("error").GetString();

    [Fact]
    public async Task Either_friend_sets_it_in_a_one_to_one_chat_and_a_line_says_who_and_how_long()
    {
        var p = await Pair();
        Assert.Equal(JsonValueKind.Null, (await Listed(p.Chat, p.CA)).GetProperty("disappearSeconds").ValueKind);

        using (var r = await Set(p.Chat, p.CB, 86_400)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(86_400, (await Listed(p.Chat, p.CA)).GetProperty("disappearSeconds").GetInt32());
        using (var r = await Set(p.Chat, p.CA, null)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Listed(p.Chat, p.CB)).GetProperty("disappearSeconds").ValueKind);

        var lines = (await History(p.Chat, p.CA)).Where(m => m.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object)
            .OrderBy(m => m.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("disappearing", lines[0].GetProperty("notice").GetProperty("type").GetString());
        Assert.Equal(86_400, lines[0].GetProperty("notice").GetProperty("seconds").GetInt32());
        Assert.Equal(p.B.Id, lines[0].GetProperty("sender").GetProperty("id").GetGuid());
        Assert.Equal(0, lines[1].GetProperty("notice").GetProperty("seconds").GetInt32());   // turned off

        // The same again changes nothing and says nothing.
        var seq = (await Listed(p.Chat, p.CA)).GetProperty("lastSeq").GetInt64();
        using (var same = await Set(p.Chat, p.CA, null)) Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);
        Assert.Equal(seq, (await Listed(p.Chat, p.CA)).GetProperty("lastSeq").GetInt64());
    }

    [Fact]
    public async Task Only_the_lengths_offered_and_in_a_group_only_the_leader_and_deputies()
    {
        var g = await Group();
        using (var odd = await Set(g.Group, g.Leader, 60)) Assert.Equal("invalid_setting", Error(await AppFixture.Json(odd)));
        using (var member = await Set(g.Group, g.Member, 3600))
        {
            Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
            Assert.Equal("not_allowed", Error(await AppFixture.Json(member)));
        }
        using (var deputy = await Set(g.Group, g.Deputy, 3600)) Assert.Equal(HttpStatusCode.NoContent, deputy.StatusCode);
        using (var leader = await Set(g.Group, g.Leader, 900)) Assert.Equal(HttpStatusCode.NoContent, leader.StatusCode);
        var (_, stranger) = await app.Member();
        using (var outsider = await Set(g.Group, stranger, 900)) Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    [Fact]
    public async Task A_message_gets_its_deadline_when_sent_and_keeps_it_when_the_setting_changes()
    {
        var p = await Pair();
        var before = await Send(p.Chat, p.CA, "kept for good");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("expiresAt").ValueKind);

        using (var r = await Set(p.Chat, p.CA, 3600)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var during = await Send(p.Chat, p.CB, "gone in an hour");
        Assert.Equal(app.Clock.Now.AddHours(1), during.GetProperty("expiresAt").GetDateTimeOffset());

        using (var r = await Set(p.Chat, p.CA, 604_800)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var history = await History(p.Chat, p.CA);
        Assert.Equal(app.Clock.Now.AddHours(1), history.Single(m => m.GetProperty("body").GetString() == "gone in an hour").GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, history.Single(m => m.GetProperty("body").GetString() == "kept for good").GetProperty("expiresAt").ValueKind);
    }

    private async Task<int> RunJob()
    {
        await using var scope = app.Factory.Services.CreateAsyncScope();
        var p = scope.ServiceProvider;
        return await Disappearing.RunOnce(p.GetRequiredService<GomDbContext>(), p.GetRequiredService<IBlobStore>(), p.GetRequiredService<LiveDelivery>(), app.Clock.Now, CancellationToken.None);
    }

    [Fact]
    public async Task The_job_deletes_a_message_for_good_at_its_deadline_with_its_reactions_and_pins_and_never_the_notices()
    {
        var p = await Pair();
        var kept = await Send(p.Chat, p.CA, "kept for good");
        using (var r = await Set(p.Chat, p.CA, 900)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var going = await Send(p.Chat, p.CB, "gone in 15 minutes");
        var goingId = going.GetProperty("id").GetGuid();
        using (var react = await app.Call(Put, $"/api/conversations/{p.Chat}/messages/{goingId}/reaction", new { emoji = "❤️" }, p.CA)) Assert.True(react.IsSuccessStatusCode);
        using (var pin = await app.Call(Put, $"/api/conversations/{p.Chat}/messages/{goingId}/pin", null, p.CA)) Assert.True(pin.IsSuccessStatusCode);
        Assert.Equal(1, (await Listed(p.Chat, p.CA)).GetProperty("unread").GetInt32());

        app.Clock.Advance(TimeSpan.FromMinutes(10));
        var later = await Send(p.Chat, p.CA, "gone at minute 25");   // in the same conversation, not due yet when the first is
        app.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(0, await RunJob());
        Assert.Contains(await History(p.Chat, p.CA), m => m.GetProperty("id").GetGuid() == goingId);

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.InRange(await RunJob(), 0, 1);   // the app's own job (every 30 s) may have been first
        var history = await History(p.Chat, p.CA);
        Assert.DoesNotContain(history, m => m.GetProperty("id").GetGuid() == goingId);
        Assert.Contains(history, m => m.GetProperty("id").GetGuid() == kept.GetProperty("id").GetGuid());
        Assert.Contains(history, m => m.GetProperty("id").GetGuid() == later.GetProperty("id").GetGuid());
        Assert.Contains(history, m => m.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object);   // the line stays
        await using var db = app.Db.NewContext();
        Assert.False(await db.Reactions.AnyAsync(r => r.MessageId == goingId));
        Assert.False(await db.Pins.AnyAsync(x => x.MessageId == goingId));
        Assert.False(await db.Events.AnyAsync(e => e.MessageId == goingId));
        var listed = await Listed(p.Chat, p.CA);
        Assert.Equal(0, listed.GetProperty("unread").GetInt32());
        Assert.Equal("gone at minute 25", listed.GetProperty("lastMessage").GetProperty("body").GetString());
    }
}
