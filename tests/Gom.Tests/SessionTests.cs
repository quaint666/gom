using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class SessionTests(AppFixture app) : IClassFixture<AppFixture>
{
    private Task<HttpResponseMessage> Call(HttpMethod method, string path, string? cookie, string? origin = AppFixture.Origin)
    {
        var client = app.Client();
        return AppFixture.Send(client, method, path, cookie: cookie, origin: origin);
    }

    [Fact]
    public async Task Me_is_401_without_a_cookie_and_with_a_made_up_one()
    {
        using var none = await Call(HttpMethod.Get, "/api/me", null);
        using var fake = await Call(HttpMethod.Get, "/api/me", "A".PadRight(43, 'A'));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fake.StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_signed_in_user()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        using var response = await Call(HttpMethod.Get, "/api/me", cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.GomId, json.GetProperty("gomId").GetString());
        Assert.True(json.TryGetProperty("isOwner", out _));
    }

    [Fact]
    public async Task Signing_out_ends_that_session_and_clears_the_cookie()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        using var out1 = await Call(HttpMethod.Delete, "/api/session", cookie);
        using var after = await Call(HttpMethod.Get, "/api/me", cookie);

        Assert.Equal(HttpStatusCode.NoContent, out1.StatusCode);
        Assert.Contains("expires=", AppFixture.SetCookieHeader(out1)!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.NotNull((await app.SessionsOf(user.Id)).Single().RevokedAt);
    }

    [Fact]
    public async Task A_session_unused_for_30_days_is_over()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        app.Clock.Advance(TimeSpan.FromDays(31));
        using var response = await Call(HttpMethod.Get, "/api/me", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Use_slides_the_expiry_and_reissues_the_cookie_but_never_past_90_days()
    {
        var user = await app.AddUser();
        var started = app.Clock.Now;
        var cookie = await app.SignIn(user.GomId);

        app.Clock.Advance(TimeSpan.FromDays(29));
        using var day29 = await Call(HttpMethod.Get, "/api/me", cookie);
        Assert.Equal(HttpStatusCode.OK, day29.StatusCode);
        Assert.NotNull(AppFixture.SetCookieHeader(day29)); // a fresh expiry for the browser
        Assert.Equal(app.Clock.Now.AddDays(30), (await app.SessionsOf(user.Id)).Single().ExpiresAt);

        app.Clock.Advance(TimeSpan.FromDays(29));
        using var day58 = await Call(HttpMethod.Get, "/api/me", cookie);
        Assert.Equal(HttpStatusCode.OK, day58.StatusCode);

        app.Clock.Advance(TimeSpan.FromDays(29));
        using var day87 = await Call(HttpMethod.Get, "/api/me", cookie);
        Assert.Equal(HttpStatusCode.OK, day87.StatusCode);
        Assert.Equal(started.AddDays(90), (await app.SessionsOf(user.Id)).Single().ExpiresAt);

        app.Clock.Advance(TimeSpan.FromDays(4));
        using var day91 = await Call(HttpMethod.Get, "/api/me", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, day91.StatusCode);
    }

    [Fact]
    public async Task Last_seen_is_not_written_on_every_request()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);
        var first = (await app.SessionsOf(user.Id)).Single().LastSeenAt;

        app.Clock.Advance(TimeSpan.FromHours(3));
        using var again = await Call(HttpMethod.Get, "/api/me", cookie);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Null(AppFixture.SetCookieHeader(again));
        Assert.Equal(first, (await app.SessionsOf(user.Id)).Single().LastSeenAt);
    }

    [Fact]
    public async Task Disabling_a_user_ends_their_sessions_at_once()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);
        await using (var db = app.Db.NewContext())
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAt, app.Clock.Now));

        using var response = await Call(HttpMethod.Get, "/api/me", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signing_out_with_the_wrong_Origin_is_refused_and_changes_nothing()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        using var response = await Call(HttpMethod.Delete, "/api/session", cookie, origin: "https://evil.example");
        using var still = await Call(HttpMethod.Get, "/api/me", cookie);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task The_list_shows_only_my_sessions_and_marks_the_current_one()
    {
        var me = await app.AddUser();
        var other = await app.AddUser();
        var phone = await app.SignIn(me.GomId, userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 Version/17.5 Mobile/15E148 Safari/604.1");
        await app.SignIn(me.GomId, userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0");
        await app.SignIn(other.GomId);

        using var response = await Call(HttpMethod.Get, "/api/sessions", phone);

        var list = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Single(list, s => s.GetProperty("current").GetBoolean());
        Assert.Contains(list, s => s.GetProperty("deviceLabel").GetString() == "Safari on iPhone" && s.GetProperty("current").GetBoolean());
        Assert.Contains(list, s => s.GetProperty("deviceLabel").GetString() == "Firefox on Windows");
        Assert.DoesNotContain("tokenHash", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task I_can_end_another_of_my_sessions()
    {
        var me = await app.AddUser();
        var phone = await app.SignIn(me.GomId);
        var laptop = await app.SignIn(me.GomId);
        var laptopId = (await app.SessionsOf(me.Id)).Single(s => s.TokenHash.SequenceEqual(Gom.Web.Auth.Tokens.Hash(laptop))).Id;

        using var ended = await Call(HttpMethod.Delete, $"/api/sessions/{laptopId}", phone);
        using var laptopNow = await Call(HttpMethod.Get, "/api/me", laptop);
        using var phoneNow = await Call(HttpMethod.Get, "/api/me", phone);

        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, laptopNow.StatusCode);
        Assert.Equal(HttpStatusCode.OK, phoneNow.StatusCode);
    }

    [Fact]
    public async Task I_cannot_end_someone_elses_session_and_cannot_tell_it_exists()
    {
        var me = await app.AddUser();
        var other = await app.AddUser();
        var mine = await app.SignIn(me.GomId);
        var theirs = await app.SignIn(other.GomId);
        var theirId = (await app.SessionsOf(other.Id)).Single().Id;

        using var attempt = await Call(HttpMethod.Delete, $"/api/sessions/{theirId}", mine);
        using var missing = await Call(HttpMethod.Delete, $"/api/sessions/{Guid.NewGuid()}", mine);
        using var theirsStill = await Call(HttpMethod.Get, "/api/me", theirs);

        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, theirsStill.StatusCode);
    }
}
