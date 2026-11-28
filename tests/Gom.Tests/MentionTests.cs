using System.Net;
using System.Text.Json;
using Gom.Web.Chat;
using Gom.Web.Data;
using static System.Net.Http.HttpMethod;

namespace Gom.Tests;

public class MentionWordsTests
{
    [Theory]
    [InlineData("@Mai", "Mai", true)]
    [InlineData("hi @Mai, ok", "Mai", true)]
    [InlineData("(@Mai)", "Mai", true)]
    [InlineData("ok.@Mai", "Mai", true)]
    [InlineData("@mai", "Mai", true)]                        // capitals do not matter
    [InlineData("@Nguyễn Văn A đâu rồi", "Nguyễn Văn A", true)]  // a name with spaces
    [InlineData("hello @Mai\n@Lan", "Lan", true)]
    [InlineData("a@Mai", "Mai", false)]                      // run into the word before it: an address, not a mention
    [InlineData("mail me@Mai.com", "Mai", false)]
    [InlineData("@ Mai", "Mai", false)]                      // the name follows the @ at once
    [InlineData("@.Mai", "Mai", false)]
    [InlineData("@Maia", "Mai", false)]                      // run into the word after it
    [InlineData("@Mai_x", "Mai", false)]
    [InlineData("@Mai1", "Mai", false)]
    [InlineData("Mai", "Mai", false)]
    [InlineData("@Mai", "", false)]
    [InlineData("@all", "all", true)]
    [InlineData("@ALL họp", "all", true)]
    [InlineData("a@all.com", "all", false)]
    [InlineData("@alloy", "all", false)]
    public void A_name_is_mentioned_only_when_written_as_a_mention(string body, string name, bool expected) =>
        Assert.Equal(expected, Mentions.Has(body, name));

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    public void All_cannot_be_taken_as_a_Gom_ID(string id) => Assert.False(Gom.Web.Auth.GomId.IsValid(id.ToLowerInvariant()));
}

public class MentionTests(AppFixture app) : IClassFixture<AppFixture>
{
    private async Task<(User A, string CA, User B, string CB, User C, string CC, Guid Chat)> Group()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        using var made = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId, c.GomId } }, ca);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        return (a, ca, b, cb, c, cc, (await AppFixture.Json(made)).GetProperty("id").GetGuid());
    }

    private async Task<JsonElement> Send(string cookie, Guid chat, string body, params Guid[] mentions)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body, mentions }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private static Guid[] Mentioned(JsonElement message) =>
        message.TryGetProperty("mentions", out var m) && m.ValueKind == JsonValueKind.Array ? m.EnumerateArray().Select(x => x.GetGuid()).ToArray() : [];

    private static bool Everyone(JsonElement message) => message.TryGetProperty("mentionsAll", out var m) && m.GetBoolean();

    private async Task<JsonElement> Listed(string cookie, Guid chat)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == chat);
    }

    private async Task<JsonElement> History(string cookie, Guid chat)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/messages", cookie: cookie);
        return await AppFixture.Json(r);
    }

    [Fact]
    public async Task A_mention_is_kept_for_a_member_named_in_the_words_and_shown_the_same_in_history()
    {
        var g = await Group();
        var sent = await Send(g.CA, g.Chat, $"@{g.B.DisplayName} xem giúp nhé", g.B.Id, g.C.Id);   // C is asked for but not written

        Assert.Equal([g.B.Id], Mentioned(sent));
        Assert.False(Everyone(sent));
        var stored = (await History(g.CC, g.Chat)).GetProperty("messages").EnumerateArray().Single();
        Assert.Equal([g.B.Id], Mentioned(stored));
    }

    [Fact]
    public async Task Nobody_is_mentioned_without_the_words_a_stranger_or_the_sender()
    {
        var g = await Group();
        var (stranger, _) = await app.Member();
        var sent = await Send(g.CA, g.Chat, $"@{stranger.DisplayName} @{g.A.DisplayName} @{g.B.DisplayName}x", stranger.Id, g.A.Id, g.B.Id);
        Assert.Empty(Mentioned(sent));
        Assert.False(sent.TryGetProperty("mentions", out var field) && field.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task At_all_mentions_everyone_when_anyone_in_the_group_writes_it()
    {
        var g = await Group();
        Assert.True(Everyone(await Send(g.CB, g.Chat, "@all họp tối nay")));
        Assert.True(Everyone(await Send(g.CC, g.Chat, "@All")));
        Assert.False(Everyone(await Send(g.CC, g.Chat, "mail a@all.com")));
        Assert.False(Everyone(await Send(g.CC, g.Chat, "tất cả all")));
    }

    [Fact]
    public async Task A_one_to_one_chat_mentions_nobody()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        var sent = await Send(ca, chat, $"@all @{b.DisplayName}", b.Id);
        Assert.Empty(Mentioned(sent));
        Assert.False(Everyone(sent));
    }

    [Fact]
    public async Task The_list_marks_an_unread_mention_with_the_first_one_until_it_is_read()
    {
        var g = await Group();
        await Send(g.CA, g.Chat, "chưa gì cả");
        var firstMessage = await Send(g.CA, g.Chat, $"@{g.B.DisplayName} đây", g.B.Id);
        var first = firstMessage.GetProperty("seq").GetInt64();
        await Send(g.CA, g.Chat, $"@{g.B.DisplayName} lần nữa", g.B.Id);

        var forB = await Listed(g.CB, g.Chat);
        Assert.True(forB.GetProperty("mentioned").GetBoolean());
        Assert.Equal(first, forB.GetProperty("firstMentionSeq").GetInt64());
        Assert.Equal(firstMessage.GetProperty("id").GetGuid(), forB.GetProperty("firstMentionId").GetGuid());
        var forC = await Listed(g.CC, g.Chat);
        Assert.False(forC.GetProperty("mentioned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, forC.GetProperty("firstMentionSeq").ValueKind);
        Assert.Equal(JsonValueKind.Null, forC.GetProperty("firstMentionId").ValueKind);
        Assert.False((await Listed(g.CA, g.Chat)).GetProperty("mentioned").GetBoolean());   // not for the one who wrote it

        using var read = await app.Call(Post, $"/api/conversations/{g.Chat}/read", new { seq = first }, g.CB);
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        var after = await Listed(g.CB, g.Chat);
        Assert.True(after.GetProperty("mentioned").GetBoolean());                    // the second is still unread
        Assert.Equal(first + 1, after.GetProperty("firstMentionSeq").GetInt64());
        using var readAll = await app.Call(Post, $"/api/conversations/{g.Chat}/read", new { seq = first + 1 }, g.CB);
        Assert.False((await Listed(g.CB, g.Chat)).GetProperty("mentioned").GetBoolean());
    }

    [Fact]
    public async Task At_all_marks_the_list_of_everyone_but_the_writer()
    {
        var g = await Group();
        await Send(g.CA, g.Chat, "@all ăn cơm");
        Assert.True((await Listed(g.CB, g.Chat)).GetProperty("mentioned").GetBoolean());
        Assert.True((await Listed(g.CC, g.Chat)).GetProperty("mentioned").GetBoolean());
        Assert.False((await Listed(g.CA, g.Chat)).GetProperty("mentioned").GetBoolean());
    }

    [Fact]
    public async Task A_mention_taken_back_or_deleted_for_you_no_longer_marks_the_list()
    {
        var g = await Group();
        var sent = await Send(g.CA, g.Chat, $"@{g.B.DisplayName} đây", g.B.Id);
        var id = sent.GetProperty("id").GetGuid();
        using (var hide = await app.Call(Put, $"/api/conversations/{g.Chat}/messages/{id}/hidden", new { hidden = true }, g.CB)) Assert.True(hide.IsSuccessStatusCode);
        Assert.False((await Listed(g.CB, g.Chat)).GetProperty("mentioned").GetBoolean());

        var again = await Send(g.CA, g.Chat, $"@{g.C.DisplayName} đây", g.C.Id);
        using (var recall = await app.Call(Delete, $"/api/conversations/{g.Chat}/messages/{again.GetProperty("id").GetGuid()}", cookie: g.CA)) Assert.Equal(HttpStatusCode.NoContent, recall.StatusCode);
        Assert.False((await Listed(g.CC, g.Chat)).GetProperty("mentioned").GetBoolean());
        var recalled = (await History(g.CC, g.Chat)).GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == again.GetProperty("id").GetGuid());
        Assert.Empty(Mentioned(recalled));
    }

    [Fact]
    public async Task Editing_changes_who_is_mentioned()
    {
        var g = await Group();
        var sent = await Send(g.CA, g.Chat, "chưa ai");
        var id = sent.GetProperty("id").GetGuid();
        async Task Edit(string body, params Guid[] mentions)
        {
            using var r = await app.Call(Patch, $"/api/conversations/{g.Chat}/messages/{id}", new { body, mentions }, g.CA);
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }
        JsonElement Now(JsonElement history) => history.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == id);

        await Edit($"@{g.B.DisplayName} và @{g.C.DisplayName}", g.B.Id, g.C.Id);
        Assert.Equivalent(new[] { g.B.Id, g.C.Id }, Mentioned(Now(await History(g.CA, g.Chat))));
        await Edit($"@{g.C.DisplayName} thôi", g.B.Id, g.C.Id);   // B is asked for but no longer written
        Assert.Equal([g.C.Id], Mentioned(Now(await History(g.CA, g.Chat))));
        await Edit("không ai nữa", g.B.Id, g.C.Id);
        Assert.Empty(Mentioned(Now(await History(g.CA, g.Chat))));
    }
}
