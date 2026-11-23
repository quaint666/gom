using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class ResetTests(AppFixture app) : IClassFixture<AppFixture>
{
    private const string NewPassword = "a brand new password";

    private async Task<string> ResetToken(string gomId)
    {
        var (_, cookie) = await app.Owner();
        using var response = await app.Call(HttpMethod.Post, $"/api/users/{gomId}/reset-link", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private async Task<OneTimeLink> LinkFor(string token)
    {
        await using var db = app.Db.NewContext();
        var hash = Tokens.Hash(token);
        return await db.Links.SingleAsync(l => l.TokenHash == hash);
    }

    [Fact]
    public async Task The_owner_makes_a_reset_link_for_a_member_that_lasts_24_hours()
    {
        var member = await app.AddUser();
        var (owner, cookie) = await app.Owner();

        using var response = await app.Call(HttpMethod.Post, $"/api/users/{member.GomId}/reset-link", cookie: cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("token").GetString()!;
        Assert.Equal("/reset/" + token, json.GetProperty("path").GetString());
        var link = await LinkFor(token);
        Assert.Equal(LinkKind.Reset, link.Kind);
        Assert.Equal(member.Id, link.TargetUserId);
        Assert.Equal(owner.Id, link.CreatedBy);
        Assert.Equal(app.Clock.Now.AddHours(24), link.ExpiresAt);
    }

    [Fact]
    public async Task A_reset_link_for_someone_who_does_not_exist_is_a_404()
    {
        var (_, cookie) = await app.Owner();
        using var response = await app.Call(HttpMethod.Post, "/api/users/nobody_at_all/reset-link", cookie: cookie);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_make_a_reset_link()
    {
        var member = await app.AddUser();
        var other = await app.AddUser();
        var cookie = await app.SignIn(member.GomId);
        using var response = await app.Call(HttpMethod.Post, $"/api/users/{other.GomId}/reset-link", cookie: cookie);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_new_reset_link_cancels_the_earlier_one()
    {
        var member = await app.AddUser();
        var first = await ResetToken(member.GomId);
        var second = await ResetToken(member.GomId);

        using var old = await app.Call(HttpMethod.Get, $"/api/reset/{first}");
        using var current = await app.Call(HttpMethod.Get, $"/api/reset/{second}");

        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task A_live_reset_link_says_whose_password_it_changes()
    {
        var member = await app.AddUser();
        var token = await ResetToken(member.GomId);

        using var response = await app.Call(HttpMethod.Get, $"/api/reset/{token}");

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(member.GomId, json.GetProperty("gomId").GetString());
    }

    [Fact]
    public async Task Resetting_sets_the_new_password_ends_every_old_session_and_signs_in_the_new_one()
    {
        var member = await app.AddUser();
        var phone = await app.SignIn(member.GomId);
        var laptop = await app.SignIn(member.GomId);
        var token = await ResetToken(member.GomId);

        using var response = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = NewPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fresh = AppFixture.CookieValue(response)!;
        using var phoneNow = await app.Call(HttpMethod.Get, "/api/me", cookie: phone);
        using var laptopNow = await app.Call(HttpMethod.Get, "/api/me", cookie: laptop);
        using var freshNow = await app.Call(HttpMethod.Get, "/api/me", cookie: fresh);
        Assert.Equal(HttpStatusCode.Unauthorized, phoneNow.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, laptopNow.StatusCode);
        Assert.Equal(HttpStatusCode.OK, freshNow.StatusCode);
        using var oldPassword = await app.Call(HttpMethod.Post, "/api/session", new { gomId = member.GomId, password = AppFixture.Password });
        using var newPassword = await app.Call(HttpMethod.Post, "/api/session", new { gomId = member.GomId, password = NewPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
        var link = await LinkFor(token);
        Assert.Equal(member.Id, link.ConsumedBy);
    }

    [Fact]
    public async Task A_reset_link_works_once()
    {
        var member = await app.AddUser();
        var token = await ResetToken(member.GomId);
        using var first = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = NewPassword });
        using var second = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = "another new password" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task A_weak_new_password_is_a_400_and_the_link_stays_usable()
    {
        var member = await app.AddUser();
        var token = await ResetToken(member.GomId);

        using var weak = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = "short" });
        using var retry = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("password_too_short", await weak.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task A_reset_link_is_dead_after_24_hours()
    {
        var member = await app.AddUser();
        var token = await ResetToken(member.GomId);
        app.Clock.Advance(TimeSpan.FromHours(25));
        using var response = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = NewPassword });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_reset_link_for_a_user_disabled_since_is_dead()
    {
        var member = await app.AddUser();
        var token = await ResetToken(member.GomId);
        await using (var db = app.Db.NewContext())
            await db.Users.Where(u => u.Id == member.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisabledAt, app.Clock.Now));
        using var response = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = NewPassword });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_invite_token_does_not_work_as_a_reset_token_and_the_other_way_round()
    {
        var member = await app.AddUser();
        var invite = await app.NewInviteToken();
        var reset = await ResetToken(member.GomId);

        using var inviteAsReset = await app.Call(HttpMethod.Post, $"/api/reset/{invite}", new { password = NewPassword });
        using var resetAsInvite = await app.Call(HttpMethod.Post, $"/api/join/{reset}", new { gomId = "someone_new_1", password = NewPassword });

        Assert.Equal(HttpStatusCode.NotFound, inviteAsReset.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, resetAsInvite.StatusCode);
    }

    [Fact]
    public async Task Resetting_from_another_site_is_refused()
    {
        var member = await app.AddUser();
        var token = await ResetToken(member.GomId);
        using var response = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = NewPassword }, origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_owner_can_list_members_and_a_member_cannot()
    {
        var member = await app.AddUser();
        var (_, ownerCookie) = await app.Owner();
        var memberCookie = await app.SignIn(member.GomId);

        using var asOwner = await app.Call(HttpMethod.Get, "/api/users", cookie: ownerCookie);
        using var asMember = await app.Call(HttpMethod.Get, "/api/users", cookie: memberCookie);

        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
        var text = await asOwner.Content.ReadAsStringAsync();
        Assert.Contains(member.GomId, text);
        Assert.DoesNotContain("passwordHash", text);
        Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
    }
}
