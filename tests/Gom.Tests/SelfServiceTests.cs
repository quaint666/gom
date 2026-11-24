using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>Changing your own nickname and password, and the owner switching a member off and on.</summary>
public class SelfServiceTests(AppFixture app) : IClassFixture<AppFixture>
{
    private const string NewPassword = "a different long password";

    private static async Task<string?> ErrorOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private async Task<string> NameOf(string cookie)
    {
        using var me = await app.Call(HttpMethod.Get, "/api/me", cookie: cookie);
        return (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayName").GetString()!;
    }

    private async Task<int> SignInStatus(string gomId, string password)
    {
        using var client = app.Client();
        using var r = await AppFixture.Send(client, HttpMethod.Post, "/api/session", new { gomId, password });
        return (int)r.StatusCode;
    }

    // ---- nickname

    [Fact]
    public async Task A_member_changes_their_nickname_and_the_Gom_ID_stays()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        using var response = await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "  Chị Hai  " }, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Chị Hai", json.GetProperty("displayName").GetString());
        Assert.Equal(user.GomId, json.GetProperty("gomId").GetString());
        Assert.Equal("Chị Hai", await NameOf(cookie));
    }

    [Fact]
    public async Task An_empty_nickname_goes_back_to_the_Gom_ID()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);
        using var set = await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "Chị Hai" }, cookie);

        using var response = await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "   " }, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(user.GomId, await NameOf(cookie));
    }

    [Fact]
    public async Task A_nickname_with_a_control_character_is_refused_and_the_old_one_stays()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        using var response = await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "bad\u0007name" }, cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_display_name", await ErrorOf(response));
        Assert.Equal(user.GomId, await NameOf(cookie));
    }

    [Fact]
    public async Task Changing_a_nickname_needs_a_session_and_a_good_Origin()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        using var anonymous = await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "x" });
        using var foreign = await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "x" }, cookie, origin: "https://evil.test");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(user.GomId, await NameOf(cookie));
    }

    [Fact]
    public async Task Who_am_I_includes_my_opaque_id_everywhere_a_person_is_returned()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        using var me = await app.Call(HttpMethod.Get, "/api/me", cookie: cookie);
        using var rename = await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "Chị Hai" }, cookie);
        using var signIn = await app.Call(HttpMethod.Post, "/api/session", new { gomId = user.GomId, password = AppFixture.Password });

        foreach (var r in new[] { me, rename, signIn })
            Assert.Equal(user.Id, (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    // ---- own password

    [Fact]
    public async Task Changing_your_password_keeps_this_device_and_signs_out_the_others()
    {
        var user = await app.AddUser();
        var here = await app.SignIn(user.GomId);
        var elsewhere = await app.SignIn(user.GomId);

        using var response = await app.Call(HttpMethod.Put, "/api/me/password", new { current = AppFixture.Password, password = NewPassword }, here);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var stillHere = await app.Call(HttpMethod.Get, "/api/me", cookie: here);
        using var gone = await app.Call(HttpMethod.Get, "/api/me", cookie: elsewhere);
        Assert.Equal(HttpStatusCode.OK, stillHere.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        Assert.Equal(200, await SignInStatus(user.GomId, NewPassword));
        Assert.Equal(401, await SignInStatus(user.GomId, AppFixture.Password));
    }

    [Fact]
    public async Task A_wrong_current_password_changes_nothing()
    {
        var user = await app.AddUser();
        var here = await app.SignIn(user.GomId);
        var elsewhere = await app.SignIn(user.GomId);

        using var response = await app.Call(HttpMethod.Put, "/api/me/password", new { current = "not my password", password = NewPassword }, here);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("wrong_password", await ErrorOf(response));
        using var other = await app.Call(HttpMethod.Get, "/api/me", cookie: elsewhere);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(200, await SignInStatus(user.GomId, AppFixture.Password));
    }

    [Fact]
    public async Task A_new_password_that_breaks_the_rules_is_refused()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        using var response = await app.Call(HttpMethod.Put, "/api/me/password", new { current = AppFixture.Password, password = "short" }, cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("password_too_short", await ErrorOf(response));
        Assert.Equal(200, await SignInStatus(user.GomId, AppFixture.Password));
    }

    [Fact]
    public async Task Missing_fields_are_refused()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);

        using var response = await app.Call(HttpMethod.Put, "/api/me/password", new { password = NewPassword }, cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("missing_fields", await ErrorOf(response));
    }

    [Fact]
    public async Task Guessing_the_current_password_is_slowed_even_with_a_stolen_session()
    {
        var user = await app.AddUser();
        var cookie = await app.SignIn(user.GomId);
        for (var i = 0; i < 3; i++)
        {
            using var wrong = await app.Call(HttpMethod.Put, "/api/me/password", new { current = "guess " + i + " nope", password = NewPassword }, cookie);
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var blocked = await app.Call(HttpMethod.Put, "/api/me/password", new { current = AppFixture.Password, password = NewPassword }, cookie);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.Equal(user.PasswordHash, (await db.Users.SingleAsync(u => u.Id == user.Id)).PasswordHash);
    }

    // ---- disabling a member

    [Fact]
    public async Task The_owner_disables_a_member_who_is_signed_out_everywhere_and_cannot_sign_in()
    {
        var member = await app.AddUser();
        var cookie = await app.SignIn(member.GomId);
        var (_, ownerCookie) = await app.Owner();

        using var response = await app.Call(HttpMethod.Post, $"/api/users/{member.GomId}/disable", cookie: ownerCookie);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var me = await app.Call(HttpMethod.Get, "/api/me", cookie: cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(401, await SignInStatus(member.GomId, AppFixture.Password));
        Assert.All(await app.SessionsOf(member.Id), s => Assert.NotNull(s.RevokedAt));
        await using var db = app.Db.NewContext();
        Assert.NotNull((await db.Users.SingleAsync(u => u.Id == member.Id)).DisabledAt);
    }

    [Fact]
    public async Task The_owner_enables_a_member_again()
    {
        var member = await app.AddUser(disabled: true);
        var (_, ownerCookie) = await app.Owner();

        using var response = await app.Call(HttpMethod.Post, $"/api/users/{member.GomId}/enable", cookie: ownerCookie);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(200, await SignInStatus(member.GomId, AppFixture.Password));
    }

    [Fact]
    public async Task The_owner_cannot_be_disabled()
    {
        var (owner, ownerCookie) = await app.Owner();

        using var response = await app.Call(HttpMethod.Post, $"/api/users/{owner.GomId}/disable", cookie: ownerCookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("cannot_disable_owner", await ErrorOf(response));
        using var me = await app.Call(HttpMethod.Get, "/api/me", cookie: ownerCookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Disabling_an_unknown_member_is_a_404_and_a_member_cannot_disable_anyone()
    {
        var member = await app.AddUser();
        var other = await app.AddUser();
        var memberCookie = await app.SignIn(member.GomId);
        var (_, ownerCookie) = await app.Owner();

        using var unknown = await app.Call(HttpMethod.Post, "/api/users/nobody_at_all/disable", cookie: ownerCookie);
        using var byMember = await app.Call(HttpMethod.Post, $"/api/users/{other.GomId}/disable", cookie: memberCookie);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        Assert.Equal(200, await SignInStatus(other.GomId, AppFixture.Password));
    }
}
