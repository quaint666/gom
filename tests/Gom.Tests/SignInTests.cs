using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gom.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class SignInTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static Task<HttpResponseMessage> Post(HttpClient c, object body, string? origin = AppFixture.Origin) =>
        AppFixture.Send(c, HttpMethod.Post, "/api/session", body, origin: origin);

    [Fact]
    public async Task A_correct_sign_in_sets_a_locked_down_cookie_and_returns_the_user()
    {
        var user = await app.AddUser();
        using var client = app.Client();

        using var response = await Post(client, new { gomId = user.GomId, password = AppFixture.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = AppFixture.SetCookieHeader(response)!;
        Assert.StartsWith("__Host-gom=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.GomId, json.GetProperty("gomId").GetString());
        Assert.Equal(user.DisplayName, json.GetProperty("displayName").GetString());
        Assert.False(json.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task The_database_stores_the_hash_of_the_token_never_the_token()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId, userAgent: "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/126.0 Mobile Safari/537.36");

        var sessions = await app.SessionsOf(user.Id);
        var session = Assert.Single(sessions);
        Assert.Equal(Tokens.Hash(cookie), session.TokenHash);
        Assert.Equal("Chrome on Android", session.DeviceLabel);
        Assert.Equal(app.Clock.Now.AddDays(30), session.ExpiresAt);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_ID_get_the_identical_answer()
    {
        var user = await app.AddUser();
        using var client = app.Client();

        using var wrong = await Post(client, new { gomId = user.GomId, password = "not the password" });
        using var unknown = await Post(client, new { gomId = "nobody_here", password = "not the password" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Contains("invalid_credentials", await wrong.Content.ReadAsStringAsync());
        Assert.Null(AppFixture.SetCookieHeader(wrong));
    }

    [Fact]
    public async Task The_Gom_ID_is_matched_ignoring_case_and_spaces_around_it()
    {
        var user = await app.AddUser();
        using var client = app.Client();

        using var response = await Post(client, new { gomId = "  " + user.GomId.ToUpperInvariant() + " ", password = AppFixture.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_user_cannot_sign_in()
    {
        var user = await app.AddUser(disabled: true);
        using var client = app.Client();

        using var response = await Post(client, new { gomId = user.GomId, password = AppFixture.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_or_wrongly_typed_body_is_a_400_not_a_crash()
    {
        using var client = app.Client();
        using var empty = await Post(client, new { });
        using var numbers = await Post(client, new { gomId = 5, password = 6 });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, numbers.StatusCode);
    }

    [Fact]
    public async Task A_hash_made_with_an_older_work_factor_is_replaced_on_sign_in()
    {
        var user = await app.AddUser();
        await using (var db = app.Db.NewContext())
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, new PasswordService(500).Hash(AppFixture.Password)));

        await app.SignIn(user.GomId);

        await using var db2 = app.Db.NewContext();
        var stored = (await db2.Users.SingleAsync(u => u.Id == user.Id)).PasswordHash;
        Assert.Equal(PasswordCheck.Ok, app.Passwords.Verify(stored, AppFixture.Password)); // OkRehash would mean it is still old
    }

    [Fact]
    public async Task Sign_in_is_refused_without_a_matching_Origin()
    {
        var user = await app.AddUser();
        using var client = app.Client();
        var body = new { gomId = user.GomId, password = AppFixture.Password };

        using var none = await Post(client, body, origin: null);
        using var other = await Post(client, body, origin: "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task Repeated_failures_get_a_429_with_a_wait_and_it_clears_with_time()
    {
        var user = await app.AddUser();
        using var client = app.Client();
        for (var i = 0; i < 3; i++)
        {
            using var bad = await Post(client, new { gomId = user.GomId, password = "wrong wrong wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        using var blocked = await Post(client, new { gomId = user.GomId, password = AppFixture.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        app.Clock.Advance(TimeSpan.FromMinutes(2));
        using var ok = await Post(client, new { gomId = user.GomId, password = AppFixture.Password });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
