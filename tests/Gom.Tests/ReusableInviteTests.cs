using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gom.Tests;

/// <summary>An invite the owner can give to several people: it works until it expires, is revoked, or has let in as many as chosen.</summary>
public class ReusableInviteTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static string NewId() => "r_" + Guid.NewGuid().ToString("N")[..10];
    private const string Password = "a fine long password";

    private Task<HttpResponseMessage> Join(string token, string? id = null) =>
        app.Call(HttpMethod.Post, $"/api/join/{token}", new { gomId = id ?? NewId(), password = Password });

    private async Task<(string Token, Guid Id)> Make(object? body = null)
    {
        var (_, cookie) = await app.Owner();
        using var r = await app.Call(HttpMethod.Post, "/api/invites", body, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = await AppFixture.Json(r);
        return (json.GetProperty("token").GetString()!, json.GetProperty("id").GetGuid());
    }

    private async Task<OneTimeLink> Row(Guid id)
    {
        await using var db = app.Db.NewContext();
        return await db.Links.SingleAsync(l => l.Id == id);
    }

    [Fact]
    public async Task Without_a_choice_an_invite_lasts_seven_days_and_lets_in_ten_people()
    {
        var (_, id) = await Make();
        var link = await Row(id);
        Assert.Equal(app.Clock.Now.AddDays(7), link.ExpiresAt);
        Assert.Equal(10, link.MaxUses);
        Assert.Equal(0, link.Uses);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(30, 100)]
    [InlineData(7, 0)]
    public async Task The_owner_chooses_the_days_and_the_number_of_people_or_any_number(int days, int uses)
    {
        var (_, id) = await Make(new { days, uses });
        var link = await Row(id);
        Assert.Equal(app.Clock.Now.AddDays(days), link.ExpiresAt);
        Assert.Equal(uses == 0 ? null : uses, link.MaxUses);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(31, 5)]
    [InlineData(7, -1)]
    [InlineData(7, 101)]
    public async Task Days_and_people_outside_the_limits_are_refused(int days, int uses)
    {
        var (_, cookie) = await app.Owner();
        using var r = await app.Call(HttpMethod.Post, "/api/invites", new { days, uses }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_invite", (await AppFixture.Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task One_link_lets_in_as_many_people_as_chosen_and_then_looks_like_any_dead_link()
    {
        var (token, id) = await Make(new { days = 7, uses = 3 });
        var joined = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var name = NewId();
            using var r = await Join(token, name);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            joined.Add(name);
            if (i < 2) { using var peek = await app.Call(HttpMethod.Get, $"/api/join/{token}"); Assert.Equal(HttpStatusCode.OK, peek.StatusCode); }
        }
        using var fourth = await Join(token);
        Assert.Equal(HttpStatusCode.NotFound, fourth.StatusCode);
        Assert.Equal("invalid_link", (await AppFixture.Json(fourth)).GetProperty("error").GetString());
        using var peekFull = await app.Call(HttpMethod.Get, $"/api/join/{token}");
        Assert.Equal(HttpStatusCode.NotFound, peekFull.StatusCode);

        var link = await Row(id);
        Assert.Equal(3, link.Uses);
        await using var db = app.Db.NewContext();
        Assert.Equal(3, await db.InviteUses.CountAsync(u => u.LinkId == id));
        // Each person who came through it is a friend of whoever made it.
        var owner = (await app.Owner()).User;
        foreach (var name in joined)
        {
            var user = await db.Users.SingleAsync(u => u.GomId == name);
            var (low, high) = Friendship.Order(owner.Id, user.Id);
            Assert.True(await db.Friendships.AnyAsync(f => f.UserLow == low && f.UserHigh == high && f.Status == FriendStatus.Accepted));
        }
    }

    [Fact]
    public async Task Of_many_people_racing_for_the_last_uses_exactly_as_many_get_in_as_there_are_left()
    {
        var (token, id) = await Make(new { days = 7, uses = 3 });
        var names = Enumerable.Range(0, 10).Select(_ => NewId()).ToList();
        var responses = await Task.WhenAll(names.Select(n => Join(token, n)));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.NotFound));
        await using var db = app.Db.NewContext();
        Assert.Equal(3, await db.Users.CountAsync(u => names.Contains(u.GomId)));   // no account was left behind by the ones turned away
        Assert.Equal(3, (await Row(id)).Uses);
        Assert.Equal(3, await db.InviteUses.CountAsync(u => u.LinkId == id));
    }

    [Fact]
    public async Task A_join_that_fails_for_a_taken_ID_does_not_use_up_a_place()
    {
        var (token, id) = await Make(new { days = 7, uses = 2 });
        var taken = (await app.AddUser()).GomId;
        using var clash = await Join(token, taken);
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(0, (await Row(id)).Uses);
        using var one = await Join(token); using var two = await Join(token);
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
    }

    [Fact]
    public async Task An_invite_for_any_number_keeps_working_until_it_expires()
    {
        var (token, _) = await Make(new { days = 1, uses = 0 });
        for (var i = 0; i < 12; i++) { using var r = await Join(token); Assert.Equal(HttpStatusCode.OK, r.StatusCode); }
        app.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        try
        {
            using var late = await Join(token);
            Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
            using var peek = await app.Call(HttpMethod.Get, $"/api/join/{token}");
            Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        }
        finally { app.Clock.Advance(-(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1))); }
    }

    [Fact]
    public async Task Revoking_stops_further_joins_and_keeps_the_people_who_already_came()
    {
        var (token, id) = await Make(new { days = 7, uses = 5 });
        var first = NewId();
        using (var r = await Join(token, first)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var (_, cookie) = await app.Owner();
        using var revoke = await app.Call(HttpMethod.Delete, $"/api/invites/{id}", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var after = await Join(token);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.True(await db.Users.AnyAsync(u => u.GomId == first));
        using var again = await app.Call(HttpMethod.Delete, $"/api/invites/{id}", cookie: cookie);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task The_owner_sees_for_each_link_how_many_came_and_who_and_its_state()
    {
        var (token, id) = await Make(new { days = 7, uses = 2 });
        var a = NewId(); var b = NewId();
        (await Join(token, a)).Dispose(); (await Join(token, b)).Dispose();
        var (revokedToken, revokedId) = await Make(new { days = 7, uses = 5 });
        var (_, cookie) = await app.Owner();
        (await app.Call(HttpMethod.Delete, $"/api/invites/{revokedId}", cookie: cookie)).Dispose();
        var (_, openId) = await Make(new { days = 7, uses = 0 });

        using var list = await app.Call(HttpMethod.Get, "/api/invites", cookie: cookie);
        var text = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(revokedToken, text);
        var items = JsonDocument.Parse(text).RootElement.EnumerateArray().ToList();
        JsonElement Of(Guid g) => items.Single(i => i.GetProperty("id").GetGuid() == g);

        var full = Of(id);
        Assert.Equal("full", full.GetProperty("status").GetString());
        Assert.Equal(2, full.GetProperty("uses").GetInt32());
        Assert.Equal(2, full.GetProperty("maxUses").GetInt32());
        Assert.Equal([a, b], full.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("gomId").GetString()!).ToArray());
        Assert.Equal("revoked", Of(revokedId).GetProperty("status").GetString());
        var open = Of(openId);
        Assert.Equal("active", open.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, open.GetProperty("maxUses").ValueKind);
    }

    [Fact]
    public async Task A_used_up_or_revoked_invite_cannot_be_revoked_again_and_an_expired_one_shows_as_expired()
    {
        var (token, id) = await Make(new { days = 1, uses = 1 });
        (await Join(token)).Dispose();
        var (_, cookie) = await app.Owner();
        using var full = await app.Call(HttpMethod.Delete, $"/api/invites/{id}", cookie: cookie);
        Assert.Equal(HttpStatusCode.NotFound, full.StatusCode);

        var (_, soonId) = await Make(new { days = 1, uses = 5 });
        app.Clock.Advance(TimeSpan.FromDays(2));
        try
        {
            using var list = await app.Call(HttpMethod.Get, "/api/invites", cookie: cookie);
            var items = (await AppFixture.Json(list)).EnumerateArray().ToList();
            Assert.Equal("expired", items.Single(i => i.GetProperty("id").GetGuid() == soonId).GetProperty("status").GetString());
        }
        finally { app.Clock.Advance(TimeSpan.FromDays(-2)); }
    }

    [Fact]
    public async Task The_database_itself_refuses_more_uses_than_places()
    {
        var (_, id) = await Make(new { days = 7, uses = 2 });
        await using var db = app.Db.NewContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"UPDATE one_time_links SET uses = 3 WHERE id = {id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_member_cannot_make_a_link_with_choices_either()
    {
        var cookie = await app.SignIn((await app.AddUser()).GomId);
        using var r = await app.Call(HttpMethod.Post, "/api/invites", new { days = 30, uses = 0 }, cookie);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }
}
