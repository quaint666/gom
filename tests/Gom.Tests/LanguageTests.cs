using System.Net;

namespace Gom.Tests;

/// <summary>Each person's language: Vietnamese unless they choose English, kept with their account for every device.</summary>
public class LanguageTests(AppFixture app) : IClassFixture<AppFixture>
{
    private async Task<string?> LanguageOf(string cookie)
    {
        using var r = await app.Call(HttpMethod.Get, "/api/me", cookie: cookie);
        return (await AppFixture.Json(r)).GetProperty("language").GetString();
    }

    [Fact]
    public async Task Everyone_starts_in_Vietnamese_and_can_choose_English_for_every_device()
    {
        var (user, cookie) = await app.Member();
        Assert.Equal("vi", await LanguageOf(cookie));
        using (var r = await app.Call(HttpMethod.Put, "/api/me/language", new { language = "en" }, cookie)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var other = await app.SignIn(user.GomId);
        Assert.Equal("en", await LanguageOf(other));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("EN")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Only_a_language_Gom_has_is_accepted(string? language)
    {
        var (_, cookie) = await app.Member();
        using var r = await app.Call(HttpMethod.Put, "/api/me/language", new { language }, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_language", (await AppFixture.Json(r)).GetProperty("error").GetString());
        Assert.Equal("vi", await LanguageOf(cookie));
    }
}
