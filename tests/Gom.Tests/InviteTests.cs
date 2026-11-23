using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class InviteTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static string NewId() => "n_" + Guid.NewGuid().ToString("N")[..10];
    private const string StrongPassword = "a fine long password";

    private Task<HttpResponseMessage> Join(string token, object body, string? origin = AppFixture.Origin) =>
        app.Call(HttpMethod.Post, $"/api/join/{token}", body, origin: origin);

    private async Task<OneTimeLink> LinkFor(string token)
    {
        await using var db = app.Db.NewContext();
        var hash = Tokens.Hash(token);
        return await db.Links.SingleAsync(l => l.TokenHash == hash);
    }

    // ---- creating invites

    [Fact]
    public async Task The_owner_creates_an_invite_that_lasts_7_days_and_only_its_hash_is_stored()
    {
        var (owner, cookie) = await app.Owner();

        using var response = await app.Call(HttpMethod.Post, "/api/invites", cookie: cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("token").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.Equal("/join/" + token, json.GetProperty("path").GetString());
        var link = await LinkFor(token);
        Assert.Equal(LinkKind.Invite, link.Kind);
        Assert.Equal(owner.Id, link.CreatedBy);
        Assert.Equal(app.Clock.Now.AddDays(7), link.ExpiresAt);
        Assert.Null(link.TargetUserId);
    }

    [Fact]
    public async Task A_member_who_is_not_the_owner_cannot_create_an_invite()
    {
        var member = await app.AddUser();
        var cookie = await app.SignIn(member.GomId);

        using var response = await app.Call(HttpMethod.Post, "/api/invites", cookie: cookie);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Nobody_signed_out_can_create_an_invite()
    {
        using var response = await app.Call(HttpMethod.Post, "/api/invites");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Creating_an_invite_from_another_site_is_refused()
    {
        var (_, cookie) = await app.Owner();
        using var response = await app.Call(HttpMethod.Post, "/api/invites", cookie: cookie, origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- looking at an invite

    [Fact]
    public async Task A_live_invite_tells_who_invited_you()
    {
        var token = await app.NewInviteToken();

        using var response = await app.Call(HttpMethod.Get, $"/api/join/{token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("the_owner", json.GetProperty("invitedBy").GetString());
    }

    [Fact]
    public async Task Unknown_expired_used_and_revoked_invites_all_look_the_same()
    {
        var expired = await app.NewInviteToken();
        app.Clock.Advance(TimeSpan.FromDays(8));
        var used = await app.NewInviteToken();
        (await Join(used, new { gomId = NewId(), password = StrongPassword })).Dispose();
        var (_, cookie) = await app.Owner();
        var revoked = await app.NewInviteToken();
        var revokedId = (await LinkFor(revoked)).Id;
        (await app.Call(HttpMethod.Delete, $"/api/invites/{revokedId}", cookie: cookie)).Dispose();

        var bodies = new List<string>();
        foreach (var token in new[] { "A".PadRight(43, 'A'), expired, used, revoked })
        {
            using var response = await app.Call(HttpMethod.Get, $"/api/join/{token}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }
        Assert.Single(bodies.Distinct());
        Assert.Contains("invalid_link", bodies[0]);
    }

    // ---- signing up

    [Fact]
    public async Task Signing_up_creates_the_member_uses_up_the_invite_and_signs_them_in()
    {
        var token = await app.NewInviteToken();
        var id = NewId();

        using var response = await Join(token, new { gomId = id, password = StrongPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = AppFixture.CookieValue(response)!;
        await using var db = app.Db.NewContext();
        var user = await db.Users.SingleAsync(u => u.GomId == id);
        Assert.False(user.IsOwner);
        Assert.Equal(id, user.DisplayName);
        Assert.Equal(PasswordCheck.Ok, app.Passwords.Verify(user.PasswordHash, StrongPassword));
        var link = await LinkFor(token);
        Assert.Equal(user.Id, link.ConsumedBy);
        Assert.Equal(app.Clock.Now, link.ConsumedAt);
        using var me = await app.Call(HttpMethod.Get, "/api/me", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task The_ID_is_lowercased_and_the_nickname_trimmed()
    {
        var token = await app.NewInviteToken();
        var id = NewId();

        using var response = await Join(token, new { gomId = "  " + id.ToUpperInvariant() + " ", password = StrongPassword, displayName = "  Mẹ Lan " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = app.Db.NewContext();
        var user = await db.Users.SingleAsync(u => u.GomId == id);
        Assert.Equal("Mẹ Lan", user.DisplayName);
    }

    [Fact]
    public async Task An_invite_works_once()
    {
        var token = await app.NewInviteToken();
        using var first = await Join(token, new { gomId = NewId(), password = StrongPassword });
        using var second = await Join(token, new { gomId = NewId(), password = StrongPassword });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Two_people_racing_for_one_invite_get_one_account()
    {
        var token = await app.NewInviteToken();
        var a = NewId();
        var b = NewId();

        var responses = await Task.WhenAll(Join(token, new { gomId = a, password = StrongPassword }), Join(token, new { gomId = b, password = StrongPassword }));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.NotFound], responses.Select(r => r.StatusCode).Order().ToArray());
        await using var db = app.Db.NewContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.GomId == a || u.GomId == b));
    }

    [Fact]
    public async Task An_ID_that_is_taken_is_a_409_and_the_invite_stays_usable()
    {
        var token = await app.NewInviteToken();
        var taken = (await app.AddUser()).GomId;

        using var clash = await Join(token, new { gomId = taken, password = StrongPassword });
        using var retry = await Join(token, new { gomId = NewId(), password = StrongPassword });

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("id_taken", await clash.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Theory]
    [InlineData("ab", StrongPassword, "invalid_gom_id")]
    [InlineData("admin", StrongPassword, "invalid_gom_id")]
    [InlineData("has space", StrongPassword, "invalid_gom_id")]
    [InlineData("fine_id_1", "short", "password_too_short")]
    [InlineData("fine_id_2", "password123", "password_too_common")]
    public async Task A_bad_ID_or_password_is_a_400_with_a_reason_and_the_invite_stays_usable(string id, string password, string error)
    {
        var token = await app.NewInviteToken();

        using var response = await Join(token, new { gomId = id, password });
        using var retry = await Join(token, new { gomId = NewId(), password = StrongPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task A_nickname_that_is_too_long_is_a_400()
    {
        var token = await app.NewInviteToken();
        using var response = await Join(token, new { gomId = NewId(), password = StrongPassword, displayName = new string('x', 61) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_display_name", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signing_up_from_another_site_is_refused()
    {
        var token = await app.NewInviteToken();
        using var response = await Join(token, new { gomId = NewId(), password = StrongPassword }, origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- the owner's list and revoking

    [Fact]
    public async Task The_owner_sees_invites_with_a_status_and_never_their_tokens()
    {
        var (_, cookie) = await app.Owner();
        var pending = await app.NewInviteToken();
        var used = await app.NewInviteToken();
        var usedId = NewId();
        (await Join(used, new { gomId = usedId, password = StrongPassword })).Dispose();

        using var response = await app.Call(HttpMethod.Get, "/api/invites", cookie: cookie);

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(pending, text);
        Assert.DoesNotContain("tokenHash", text);
        var list = JsonDocument.Parse(text).RootElement.EnumerateArray().ToList();
        Assert.Contains(list, i => i.GetProperty("id").GetGuid() == LinkFor(pending).Result.Id && i.GetProperty("status").GetString() == "pending");
        Assert.Contains(list, i => i.GetProperty("status").GetString() == "used" && i.GetProperty("usedBy").GetString() == usedId);
    }

    [Fact]
    public async Task A_member_cannot_list_invites()
    {
        var cookie = await app.SignIn((await app.AddUser()).GomId);
        using var response = await app.Call(HttpMethod.Get, "/api/invites", cookie: cookie);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Revoking_a_pending_invite_kills_it_and_a_used_one_cannot_be_revoked()
    {
        var (_, cookie) = await app.Owner();
        var pending = await app.NewInviteToken();
        var used = await app.NewInviteToken();
        (await Join(used, new { gomId = NewId(), password = StrongPassword })).Dispose();

        using var revoke = await app.Call(HttpMethod.Delete, $"/api/invites/{(await LinkFor(pending)).Id}", cookie: cookie);
        using var afterwards = await Join(pending, new { gomId = NewId(), password = StrongPassword });
        using var revokeUsed = await app.Call(HttpMethod.Delete, $"/api/invites/{(await LinkFor(used)).Id}", cookie: cookie);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterwards.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokeUsed.StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_revoke_an_invite()
    {
        var token = await app.NewInviteToken();
        var cookie = await app.SignIn((await app.AddUser()).GomId);
        using var response = await app.Call(HttpMethod.Delete, $"/api/invites/{(await LinkFor(token)).Id}", cookie: cookie);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var still = await app.Call(HttpMethod.Get, $"/api/join/{token}");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }
}
