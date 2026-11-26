using System.Net;
using System.Text.Json;
using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>The app with a daily cap small enough to reach, for the one test about it.</summary>
public sealed class ContactCapFixture : AppFixture
{
    protected override void Settings(IWebHostBuilder b) => b.UseSetting("Contact:DailyMax", "3");
}

/// <summary>Writing to the owner from the privacy page: anyone may, only the owner reads, and nothing is kept for long.</summary>
public class ContactTests(AppFixture app) : IClassFixture<AppFixture>
{
    /// <summary>A message, a day after the last, so neither the throttle per address nor the daily cap gets in a test's way.</summary>
    private async Task<HttpResponseMessage> Write(object body, string? cookie = null)
    {
        app.Clock.Advance(TimeSpan.FromDays(1));
        return await app.Call(HttpMethod.Post, "/api/contact", body, cookie);
    }

    private async Task<List<JsonElement>> Listed()
    {
        var (_, owner) = await app.Owner();
        using var r = await app.Call(HttpMethod.Get, "/api/contact", cookie: owner);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().ToList();
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    [Fact]
    public async Task Anyone_may_write_without_signing_in_and_the_owner_reads_it_with_how_to_answer()
    {
        var words = "Xin xóa ảnh của tôi " + Guid.NewGuid().ToString("N")[..6] + "\nCảm ơn";
        using (var r = await Write(new { body = "  " + words + "  ", replyTo = " an@example.com " })) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var found = (await Listed()).Single(m => m.GetProperty("body").GetString() == words);
        Assert.Equal("an@example.com", found.GetProperty("replyTo").GetString());
    }

    [Fact]
    public async Task A_way_to_answer_is_optional()
    {
        var words = "Không cần trả lời " + Guid.NewGuid().ToString("N")[..6];
        using (var r = await Write(new { body = words, replyTo = "   " })) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Listed()).Single(m => m.GetProperty("body").GetString() == words).GetProperty("replyTo").ValueKind);
    }

    [Fact]
    public async Task Newest_first()
    {
        var first = "Trước " + Guid.NewGuid().ToString("N")[..6];
        var second = "Sau " + Guid.NewGuid().ToString("N")[..6];
        using (await Write(new { body = first })) { }
        using (await Write(new { body = second })) { }
        var bodies = (await Listed()).Select(m => m.GetProperty("body").GetString()).ToList();
        Assert.True(bodies.IndexOf(second) < bodies.IndexOf(first));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\u0007b")]          // a control character
    [InlineData("a‎b")]          // an invisible formatting character
    public async Task A_message_must_say_something_readable(string body)
    {
        using var r = await Write(new { body });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_contact", await ErrorOf(r));
    }

    [Fact]
    public async Task At_most_2000_characters_as_a_person_counts_them()
    {
        using (var r = await Write(new { body = new string('a', 2001) })) Assert.Equal("invalid_contact", await ErrorOf(r));
        // 2000 flags, each two code units and more: still 2000 characters
        using (var r = await Write(new { body = string.Concat(Enumerable.Repeat("🇻🇳", 2000)) })) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    [Fact]
    public async Task The_way_to_answer_is_one_line_of_at_most_200_characters()
    {
        using (var r = await Write(new { body = "Chào", replyTo = "an@example.com\nb" })) Assert.Equal("invalid_contact", await ErrorOf(r));
        using (var r = await Write(new { body = "Chào", replyTo = new string('a', 201) })) Assert.Equal("invalid_contact", await ErrorOf(r));
        using (var r = await Write(new { body = "Chào", replyTo = new string('a', 200) })) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    [Fact]
    public async Task A_bot_that_fills_the_hidden_field_is_told_it_went_and_nothing_is_kept()
    {
        var words = "Mua thuốc giá rẻ " + Guid.NewGuid().ToString("N")[..6];
        using (var r = await Write(new { body = words, website = "https://spam.example" })) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.DoesNotContain(await Listed(), m => m.GetProperty("body").GetString() == words);
    }

    [Fact]
    public async Task Another_site_cannot_post_one()
    {
        app.Clock.Advance(TimeSpan.FromDays(1));
        using var r = await app.Call(HttpMethod.Post, "/api/contact", new { body = "Chào" }, origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_reads_or_deletes_them()
    {
        var (_, member) = await app.Member();
        using (var r = await app.Call(HttpMethod.Get, "/api/contact", cookie: member)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        using (var r = await app.Call(HttpMethod.Get, "/api/contact")) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using (var r = await app.Call(HttpMethod.Delete, $"/api/contact/{Guid.NewGuid()}", cookie: member)) Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task The_owner_deletes_one()
    {
        var words = "Để xóa " + Guid.NewGuid().ToString("N")[..6];
        using (await Write(new { body = words })) { }
        var id = (await Listed()).Single(m => m.GetProperty("body").GetString() == words).GetProperty("id").GetGuid();
        var (_, owner) = await app.Owner();
        using (var r = await app.Call(HttpMethod.Delete, $"/api/contact/{id}", cookie: owner)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.DoesNotContain(await Listed(), m => m.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task One_address_writing_fast_is_slowed()
    {
        app.Clock.Advance(TimeSpan.FromDays(1));
        using var client = app.Client();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contact") { Content = System.Net.Http.Json.JsonContent.Create(new { body = "Lần " + i }) };
            request.Headers.Add("Origin", AppFixture.Origin);
            request.Headers.Add("X-Test-Ip", "203.0.113.7");   // one address (the test server gives each request its own)
            using var r = await client.SendAsync(request);
            codes.Add(r.StatusCode);
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.Equal("too_many_attempts", await ErrorOf(r));
                Assert.NotNull(r.Headers.RetryAfter);
            }
        }
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
    }

    [Fact]
    public async Task Messages_past_their_time_are_deleted_and_never_listed()
    {
        var now = app.Clock.GetUtcNow();
        var old = new ContactMessage { Id = Guid.NewGuid(), Body = "Cũ", CreatedAt = now.AddDays(-181) };
        var kept = new ContactMessage { Id = Guid.NewGuid(), Body = "Còn", CreatedAt = now.AddDays(-179) };
        await using (var db = app.Db.NewContext()) { db.ContactMessages.AddRange(old, kept); await db.SaveChangesAsync(); }

        Assert.DoesNotContain(await Listed(), m => m.GetProperty("id").GetGuid() == old.Id);   // listed or not, by its age alone
        await using (var db = app.Db.NewContext())
        {
            Assert.True(await ContactCleaner.RunOnce(db, now, 180, default) >= 1);
            Assert.False(await db.ContactMessages.AnyAsync(m => m.Id == old.Id));
            Assert.True(await db.ContactMessages.AnyAsync(m => m.Id == kept.Id));
        }
    }
}

public class ContactCapTests(ContactCapFixture app) : IClassFixture<ContactCapFixture>
{
    [Fact]
    public async Task Past_the_daily_cap_everyone_is_told_to_try_later_until_a_day_has_passed()
    {
        app.Clock.Advance(TimeSpan.FromDays(2));
        async Task<HttpResponseMessage> Write()
        {
            app.Clock.Advance(TimeSpan.FromMinutes(16));   // past the throttle's memory, so only the cap counts
            return await app.Call(HttpMethod.Post, "/api/contact", new { body = "Chào" });
        }
        for (var i = 0; i < 3; i++) using (var r = await Write()) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        using (var r = await Write())
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
            Assert.Equal("contact_full", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        app.Clock.Advance(TimeSpan.FromDays(1));
        using (var r = await Write()) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }
}
