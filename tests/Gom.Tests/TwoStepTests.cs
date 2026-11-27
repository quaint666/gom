using System.Net;
using System.Text.Json;
using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class TwoStepTests(AppFixture app) : IClassFixture<AppFixture>
{
    /// <summary>RFC 6238's own test key (appendix B, SHA-1) and the codes it gives, cut to 6 digits.</summary>
    private static readonly byte[] RfcKey = "12345678901234567890"u8.ToArray();

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Codes_are_those_of_RFC_6238(long unixSeconds, string code) =>
        Assert.Equal(code, Totp.Code(RfcKey, unixSeconds / 30));

    [Fact]
    public void A_code_is_taken_one_step_early_or_late_but_not_two_and_never_at_or_before_the_last_step_used()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_111_111_111);
        var step = Totp.StepAt(now);
        Assert.Equal(step, Totp.Match(RfcKey, Totp.Code(RfcKey, step), now, 0));
        Assert.Equal(step - 1, Totp.Match(RfcKey, Totp.Code(RfcKey, step - 1), now, 0));
        Assert.Equal(step + 1, Totp.Match(RfcKey, Totp.Code(RfcKey, step + 1), now, 0));
        Assert.Null(Totp.Match(RfcKey, Totp.Code(RfcKey, step - 2), now, 0));
        Assert.Null(Totp.Match(RfcKey, Totp.Code(RfcKey, step + 2), now, 0));
        Assert.Null(Totp.Match(RfcKey, Totp.Code(RfcKey, step), now, step));
        var spaced = Totp.Code(RfcKey, step).Insert(3, " ");
        Assert.Equal(step, Totp.Match(RfcKey, spaced, now, 0));
        Assert.Null(Totp.Match(RfcKey, "12345", now, 0));
        Assert.Null(Totp.Match(RfcKey, "abcdef", now, 0));
        Assert.Null(Totp.Match(RfcKey, null, now, 0));
    }

    [Fact]
    public void The_key_is_written_in_Base32_as_authenticator_apps_take_it_and_read_back()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(RfcKey));
        Assert.Equal(RfcKey, Totp.FromBase32("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"));
        Assert.Null(Totp.FromBase32("GEZDGNBV"));        // too short to be a key
        Assert.Null(Totp.FromBase32("gezdgnbvgy3tqojqgezdgnbvgy3tqojq"));
        Assert.Equal("otpauth://totp/Gom:bao?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Gom&digits=6&period=30", Totp.Uri("bao", RfcKey));
    }

    /// <summary>A new key from the server, as Settings asks for it.</summary>
    private async Task<(string Key, byte[] Bytes)> NewKey(string cookie)
    {
        using var response = await app.Call(HttpMethod.Get, "/api/me/two-step/new", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await AppFixture.Json(response);
        var key = json.GetProperty("key").GetString()!;
        Assert.StartsWith("otpauth://totp/Gom:", json.GetProperty("uri").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("data:image/png;base64,", json.GetProperty("qr").GetString(), StringComparison.Ordinal);
        return (key, Totp.FromBase32(key)!);
    }

    private string CodeNow(byte[] key, int stepsAhead = 0) => Totp.Code(key, Totp.StepAt(app.Clock.Now) + stepsAhead);

    /// <summary>A member with two-step sign-in on; the clock is moved past the step used to turn it on.</summary>
    private async Task<(User User, string Cookie, byte[] Key)> WithTwoStep()
    {
        var (user, cookie) = await app.Member();
        var (key, bytes) = await NewKey(cookie);
        using var on = await app.Call(HttpMethod.Put, "/api/me/two-step", new { key, code = CodeNow(bytes), password = AppFixture.Password }, cookie);
        Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        app.Clock.Advance(TimeSpan.FromSeconds(30));
        return (user, cookie, bytes);
    }

    private Task<HttpResponseMessage> SignIn(string gomId, string? code = null, string password = AppFixture.Password) =>
        app.Call(HttpMethod.Post, "/api/session", code is null ? new { gomId, password } : new { gomId, password, code });

    private static async Task<string?> Error(HttpResponseMessage response) =>
        (await AppFixture.Json(response)).GetProperty("error").GetString();

    private async Task<bool> IsOn(Guid userId)
    {
        await using var db = app.Db.NewContext();
        return await db.Users.Where(u => u.Id == userId).Select(u => u.TwoStepKey != null).SingleAsync();
    }

    [Fact]
    public async Task Turning_it_on_takes_the_password_and_a_code_from_the_new_key_and_me_then_says_so()
    {
        var (user, cookie) = await app.Member();
        var (key, bytes) = await NewKey(cookie);

        using (var wrongPassword = await app.Call(HttpMethod.Put, "/api/me/two-step", new { key, code = CodeNow(bytes), password = "not the password" }, cookie))
            Assert.Equal("wrong_password", await Error(wrongPassword));
        using (var wrongCode = await app.Call(HttpMethod.Put, "/api/me/two-step", new { key, code = CodeNow(bytes, 3), password = AppFixture.Password }, cookie))
            Assert.Equal("invalid_code", await Error(wrongCode));
        Assert.False(await IsOn(user.Id));

        using (var on = await app.Call(HttpMethod.Put, "/api/me/two-step", new { key, code = CodeNow(bytes), password = AppFixture.Password }, cookie))
            Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        Assert.True(await IsOn(user.Id));
        using var me = await app.Call(HttpMethod.Get, "/api/me", cookie: cookie);
        Assert.True((await AppFixture.Json(me)).GetProperty("twoStep").GetBoolean());

        using var again = await app.Call(HttpMethod.Put, "/api/me/two-step", new { key, code = CodeNow(bytes, 1), password = AppFixture.Password }, cookie);
        Assert.Equal("two_step_on", await Error(again));
    }

    [Fact]
    public async Task Signing_in_then_asks_for_the_code_after_the_right_password_only_and_takes_each_code_once()
    {
        var (user, _, key) = await WithTwoStep();

        using (var wrongPassword = await SignIn(user.GomId, CodeNow(key), "not the password"))
            Assert.Equal("invalid_credentials", await Error(wrongPassword));
        using (var noCode = await SignIn(user.GomId))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
            Assert.Equal("code_required", await Error(noCode));
            Assert.Null(AppFixture.CookieValue(noCode));
        }
        using (var wrongCode = await SignIn(user.GomId, CodeNow(key, 5)))
            Assert.Equal("invalid_code", await Error(wrongCode));

        var code = CodeNow(key);
        using (var right = await SignIn(user.GomId, code))
        {
            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
            Assert.NotNull(AppFixture.CookieValue(right));
        }
        using var replay = await SignIn(user.GomId, code);
        Assert.Equal("invalid_code", await Error(replay));
    }

    [Fact]
    public async Task Of_two_sign_ins_racing_with_the_same_code_only_one_gets_it()
    {
        var (user, _, key) = await WithTwoStep();
        long lastUsed;
        await using (var read = app.Db.NewContext()) lastUsed = await read.Users.Where(u => u.Id == user.Id).Select(u => u.TwoStepLastStep).SingleAsync();
        var code = CodeNow(key);
        // Both read the row before either wrote: the database decides.
        await using var db = app.Db.NewContext();
        Assert.True(await TwoStepEndpoints.Use(db, user.Id, key, lastUsed, code, app.Clock.Now, CancellationToken.None));
        Assert.False(await TwoStepEndpoints.Use(db, user.Id, key, lastUsed, code, app.Clock.Now, CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_codes_count_as_wrong_tries_so_guessing_is_slowed_down()
    {
        var (user, _, key) = await WithTwoStep();
        var wrong = CodeNow(key, 5);
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 5; i++)
        {
            using var r = await SignIn(user.GomId, wrong);
            last = r.StatusCode;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public async Task Turning_it_off_takes_the_password_and_a_current_code()
    {
        var (user, cookie, key) = await WithTwoStep();

        using (var noCode = await app.Call(HttpMethod.Post, "/api/me/two-step/off", new { code = CodeNow(key, 4), password = AppFixture.Password }, cookie))
            Assert.Equal("invalid_code", await Error(noCode));
        Assert.True(await IsOn(user.Id));

        using (var off = await app.Call(HttpMethod.Post, "/api/me/two-step/off", new { code = CodeNow(key), password = AppFixture.Password }, cookie))
            Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.False(await IsOn(user.Id));
        using var plain = await SignIn(user.GomId);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
    }

    [Fact]
    public async Task The_owners_reset_link_turns_it_off()
    {
        var (user, _, _) = await WithTwoStep();
        var (_, owner) = await app.Owner();
        using var link = await app.Call(HttpMethod.Post, $"/api/users/{user.GomId}/reset-link", cookie: owner);
        var token = (await AppFixture.Json(link)).GetProperty("token").GetString();

        using var reset = await app.Call(HttpMethod.Post, $"/api/reset/{token}", new { password = "a brand new password" });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(await IsOn(user.Id));
        using var signIn = await SignIn(user.GomId, password: "a brand new password");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    }

    [Fact]
    public async Task The_server_command_turns_it_off_for_one_account_and_says_what_it_did()
    {
        var (user, _, _) = await WithTwoStep();
        await using var db = app.Db.NewContext();

        Assert.Equal($"Two-step sign-in is now off for {user.GomId}.", await TwoStepEndpoints.TurnOffFromCommand(db, user.GomId.ToUpperInvariant(), CancellationToken.None));
        Assert.False(await IsOn(user.Id));
        Assert.Equal($"{user.GomId} has no two-step sign-in on (or no such account).", await TwoStepEndpoints.TurnOffFromCommand(db, user.GomId, CancellationToken.None));
        Assert.Equal("Usage: --two-step-off <gomId>", await TwoStepEndpoints.TurnOffFromCommand(db, null, CancellationToken.None));
    }

    [Fact]
    public async Task Everything_here_needs_a_signed_in_person()
    {
        using var key = await app.Call(HttpMethod.Get, "/api/me/two-step/new");
        using var on = await app.Call(HttpMethod.Put, "/api/me/two-step", new { key = "x", code = "123456", password = "x" });
        using var off = await app.Call(HttpMethod.Post, "/api/me/two-step/off", new { code = "123456", password = "x" });
        Assert.All(new[] { key, on, off }, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }
}
