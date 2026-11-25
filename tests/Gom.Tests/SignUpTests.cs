using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Gom.Tests;

/// <summary>The app with a small waiting list, so a test can fill it.</summary>
public sealed class SignUpFixture : AppFixture
{
    protected override void Settings(IWebHostBuilder b) => b.UseSetting("Auth:MaxWaitingRequests", "3");
}

/// <summary>Asking to join: open only when the owner says so, a waiting account that can do nothing, and the owner's answers.</summary>
public class SignUpTests(SignUpFixture app) : IClassFixture<SignUpFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete;

    /// <summary>Opens or closes sign-up; opening also refuses whatever an earlier test left waiting, so the waiting list starts empty.</summary>
    private async Task SetOpen(bool open)
    {
        var (_, owner) = await app.Owner();
        using var r = await app.Call(Put, "/api/sign-up-open", new { open }, owner);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        if (!open) return;
        foreach (var x in (await Requests()).GetProperty("requests").EnumerateArray().Where(x => !x.GetProperty("refused").GetBoolean()))
            await OwnerDoes(Post, $"/api/join-requests/{x.GetProperty("gomId").GetString()}/refuse");
    }

    private static string NewId() => "r" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>A sign-up, a day after the last one, so the throttle per address never gets in the way of a test that is not about it.</summary>
    private async Task<HttpResponseMessage> Ask(string gomId, string? note = "Con của cô Lan", string? name = null)
    {
        app.Clock.Advance(TimeSpan.FromDays(1));
        return await app.Call(Post, "/api/sign-up", new { gomId, password = AppFixture.Password, displayName = name, note });
    }

    private async Task<string> Asked(string gomId, string name = "Bi")
    {
        using var r = await Ask(gomId, name: name);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return AppFixture.CookieValue(r)!;
    }

    private async Task<JsonElement> Me(string cookie)
    {
        using var r = await app.Call(Get, "/api/me", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private async Task<JsonElement> Requests()
    {
        var (_, owner) = await app.Owner();
        using var r = await app.Call(Get, "/api/join-requests", cookie: owner);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private async Task<HttpStatusCode> OwnerDoes(HttpMethod method, string path)
    {
        var (_, owner) = await app.Owner();
        using var r = await app.Call(method, path, cookie: owner);
        return r.StatusCode;
    }

    [Fact]
    public async Task Closed_until_the_owner_opens_it_and_only_the_owner_can()
    {
        await SetOpen(false);
        using (var r = await app.Call(Get, "/api/sign-up")) Assert.False((await AppFixture.Json(r)).GetProperty("open").GetBoolean());
        using (var r = await Ask(NewId()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("sign_up_closed", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        var (_, member) = await app.Member();
        using (var r = await app.Call(Put, "/api/sign-up-open", new { open = true }, member)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        await SetOpen(true);
        using (var r = await app.Call(Get, "/api/sign-up")) Assert.True((await AppFixture.Json(r)).GetProperty("open").GetBoolean());
        Assert.True((await Requests()).GetProperty("open").GetBoolean());
    }

    [Fact]
    public async Task A_request_needs_a_note_and_the_rules_of_any_account()
    {
        await SetOpen(true);
        foreach (var (note, gomId, error) in new (string?, string, string)[]
        {
            (null, NewId(), "invalid_note"), ("   ", NewId(), "invalid_note"), (new string('a', 201), NewId(), "invalid_note"), ("ai​đó", NewId(), "invalid_note"),
            ("Bi", "Has Spaces", "invalid_gom_id"),
        })
        {
            using var r = await Ask(gomId, note);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal(error, (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        using var ok = await Ask(NewId(), string.Concat(Enumerable.Repeat("🇻🇳", 200)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);   // 200 flags are 200 characters as a person counts them
    }

    [Fact]
    public async Task A_waiting_account_is_signed_in_but_can_do_nothing_and_cannot_be_found()
    {
        await SetOpen(true);
        var id = NewId();
        var cookie = await Asked(id);
        Assert.Equal("pending", (await Me(cookie)).GetProperty("standing").GetString());

        foreach (var (method, path) in new[] { (Get, "/api/conversations"), (Get, "/api/friends"), (Get, "/api/push"), (Get, "/ws") })
        {
            using var r = await app.Call(method, path, cookie: cookie);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            if (path != "/ws") Assert.Equal("pending_approval", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }

        var (_, member) = await app.Member();
        using (var r = await app.Call(Post, "/api/friends/requests", new { gomId = id }, member))
            Assert.Equal("user_not_found", (await AppFixture.Json(r)).GetProperty("error").GetString());
        var (_, owner) = await app.Owner();
        using (var r = await app.Call(Get, "/api/users", cookie: owner))
            Assert.DoesNotContain((await AppFixture.Json(r)).EnumerateArray(), u => u.GetProperty("gomId").GetString() == id);
        var listed = (await Requests()).GetProperty("requests").EnumerateArray().Single(x => x.GetProperty("gomId").GetString() == id);
        Assert.Equal("Con của cô Lan", listed.GetProperty("note").GetString());
        Assert.False(listed.GetProperty("refused").GetBoolean());

        using var off = await app.Call(Delete, "/api/session", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
    }

    [Fact]
    public async Task Approved_it_is_a_member_and_the_owner_s_friend()
    {
        await SetOpen(true);
        var id = NewId();
        var cookie = await Asked(id);
        Assert.Equal(HttpStatusCode.NoContent, await OwnerDoes(Post, $"/api/join-requests/{id}/approve"));
        Assert.Equal("member", (await Me(cookie)).GetProperty("standing").GetString());
        var (owner, _) = await app.Owner();
        using var friends = await app.Call(Get, "/api/friends", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, friends.StatusCode);
        Assert.Contains(owner.GomId, await friends.Content.ReadAsStringAsync());
        Assert.DoesNotContain((await Requests()).GetProperty("requests").EnumerateArray(), x => x.GetProperty("gomId").GetString() == id);
        Assert.Equal(HttpStatusCode.NotFound, await OwnerDoes(Post, $"/api/join-requests/{id}/approve"));   // a member is no request
    }

    [Fact]
    public async Task Refused_the_person_is_told_and_the_owner_can_still_approve_or_delete_it_which_frees_the_id()
    {
        await SetOpen(true);
        var id = NewId();
        await Asked(id);
        Assert.Equal(HttpStatusCode.NoContent, await OwnerDoes(Post, $"/api/join-requests/{id}/refuse"));
        var again = await app.SignIn(id);   // a refused request can still sign in, to be told
        Assert.Equal("refused", (await Me(again)).GetProperty("standing").GetString());
        Assert.True((await Requests()).GetProperty("requests").EnumerateArray().Single(x => x.GetProperty("gomId").GetString() == id).GetProperty("refused").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, await OwnerDoes(Delete, $"/api/join-requests/{id}"));
        using (var r = await app.Call(Get, "/api/me", cookie: again)) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        await Asked(id);   // the Gom ID is free again
    }

    [Fact]
    public async Task The_person_can_withdraw_their_own_request_and_a_member_cannot_use_that()
    {
        await SetOpen(true);
        var id = NewId();
        var cookie = await Asked(id);
        using (var r = await app.Call(Delete, "/api/sign-up", cookie: cookie)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        using (var r = await app.Call(Post, "/api/session", new { gomId = id, password = AppFixture.Password })) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var (_, member) = await app.Member();
        using (var r = await app.Call(Delete, "/api/sign-up", cookie: member)) Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        using (var r = await app.Call(Get, "/api/me", cookie: member)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task The_waiting_list_has_a_limit_and_refused_requests_do_not_count()
    {
        await SetOpen(true);
        for (var i = 0; i < 3; i++) await Asked(NewId());
        using (var r = await Ask(NewId()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("sign_up_full", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        var first = (await Requests()).GetProperty("requests").EnumerateArray().First(x => !x.GetProperty("refused").GetBoolean()).GetProperty("gomId").GetString();
        await OwnerDoes(Post, $"/api/join-requests/{first}/refuse");
        await Asked(NewId());
    }

    [Fact]
    public async Task One_address_signing_up_again_and_again_is_slowed()
    {
        await SetOpen(true);
        app.Clock.Advance(TimeSpan.FromDays(1));
        var codes = new List<HttpStatusCode>();
        using var one = app.Client();   // one address (each new client is another)
        for (var i = 0; i < 5; i++)
        {
            using var r = await AppFixture.Send(one, Post, "/api/sign-up", new { gomId = NewId(), password = "short", note = "Bi" });
            codes.Add(r.StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }
}
