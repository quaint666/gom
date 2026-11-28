using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>
/// The Android app asks the server for a notification's words (GET /api/push/notice), since FCM brings it only ids: worded as a
/// browser's notification is, at the app's level and in its person's language, and only for what that person can see.
/// </summary>
public class NoticeTests(CallFixture app) : IClassFixture<CallFixture>
{
    private async Task<(User A, string CA, User B, string CB, Guid Chat)> Pair()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return (a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private async Task<Guid> Say(string cookie, Guid chat, string body)
    {
        using var r = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Notice(string cookie, string query)
    {
        using var r = await app.Call(HttpMethod.Get, "/api/push/notice?" + query, cookie: cookie);
        return (r.StatusCode, r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.BadRequest ? await AppFixture.Json(r) : default);
    }

    private static (string, string, string, string) Parts(JsonElement n) =>
        (n.GetProperty("title").GetString()!, n.GetProperty("body").GetString()!, n.GetProperty("url").GetString()!, n.GetProperty("tag").GetString()!);

    [Fact]
    public async Task At_the_words_level_it_says_who_wrote_and_what()
    {
        var p = await Pair();
        await app.App(p.CA, "words");
        var id = await Say(p.CB, p.Chat, "hẹn 7 giờ nhé");
        var (status, n) = await Notice(p.CA, $"c={p.Chat}&m={id}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((p.B.DisplayName, "hẹn 7 giờ nhé", $"/c/{p.Chat}", p.Chat.ToString()), Parts(n));
    }

    [Fact]
    public async Task At_the_name_level_it_names_the_writer_but_not_the_words()
    {
        var p = await Pair();
        await app.App(p.CA, "name");
        var id = await Say(p.CB, p.Chat, "bí mật");
        var (_, n) = await Notice(p.CA, $"c={p.Chat}&m={id}");
        Assert.Equal((p.B.DisplayName, "Tin nhắn mới"), (n.GetProperty("title").GetString(), n.GetProperty("body").GetString()));
    }

    [Fact]
    public async Task At_the_nothing_level_or_with_no_app_it_names_nobody()
    {
        var p = await Pair();
        var id = await Say(p.CB, p.Chat, "bí mật");
        var (_, unregistered) = await Notice(p.CA, $"c={p.Chat}&m={id}");
        Assert.Equal(("Gom", "Tin nhắn mới"), (unregistered.GetProperty("title").GetString(), unregistered.GetProperty("body").GetString()));
        await app.App(p.CA, "nothing");
        var (_, nothing) = await Notice(p.CA, $"c={p.Chat}&m={id}");
        Assert.Equal(("Gom", "Tin nhắn mới"), (nothing.GetProperty("title").GetString(), nothing.GetProperty("body").GetString()));
    }

    [Fact]
    public async Task A_message_that_names_the_person_is_worded_so_at_each_level_and_one_that_says_at_all_is_worded_for_everyone()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        using var made = await app.Call(HttpMethod.Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId, c.GomId } }, ca);
        var chat = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        using var named = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = $"@{b.DisplayName} họp", mentions = new[] { b.Id } }, ca);
        var idNamed = (await AppFixture.Json(named)).GetProperty("id").GetGuid();
        var idAll = await Say(ca, chat, "@all ăn cơm");

        await app.App(cb, "words");
        var (_, forB) = await Notice(cb, $"c={chat}&m={idNamed}");
        Assert.Equal(($"{a.DisplayName} đã nhắc đến bạn: @{b.DisplayName} họp"), forB.GetProperty("body").GetString());
        await app.App(cc, "words");
        var (_, forC) = await Notice(cc, $"c={chat}&m={idNamed}");   // C is not named
        Assert.Equal($"{a.DisplayName}: @{b.DisplayName} họp", forC.GetProperty("body").GetString());
        var (_, everyone) = await Notice(cb, $"c={chat}&m={idAll}");
        Assert.Equal($"{a.DisplayName} nhắc mọi người: @all ăn cơm", everyone.GetProperty("body").GetString());

        await app.App(cb, "nothing");
        var (_, nameless) = await Notice(cb, $"c={chat}&m={idNamed}");
        Assert.Equal(("Gom", "Có người nhắc đến bạn"), (nameless.GetProperty("title").GetString(), nameless.GetProperty("body").GetString()));
        var (_, namelessAll) = await Notice(cb, $"c={chat}&m={idAll}");
        Assert.Equal("Tin nhắn mới", namelessAll.GetProperty("body").GetString());
    }

    [Fact]
    public async Task In_a_group_the_title_is_the_group_and_the_writer_leads_the_words()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        var (c, _) = await app.Member();
        await app.Befriend(a, b);
        await app.Befriend(a, c);
        Guid group;
        using (var r = await app.Call(HttpMethod.Post, "/api/conversations/group", new { title = "Nhà mình", members = new[] { b.GomId, c.GomId } }, ca))
            group = (await AppFixture.Json(r)).GetProperty("id").GetGuid();
        await app.App(ca, "words");
        var id = await Say(cb, group, "ăn cơm chưa");
        var (_, n) = await Notice(ca, $"c={group}&m={id}");
        Assert.Equal(("Nhà mình", $"{b.DisplayName}: ăn cơm chưa"), (n.GetProperty("title").GetString(), n.GetProperty("body").GetString()));
    }

    [Fact]
    public async Task A_message_taken_back_says_so_and_never_its_words()
    {
        var p = await Pair();
        await app.App(p.CA, "words");
        var id = await Say(p.CB, p.Chat, "gửi nhầm");
        using (var r = await app.Call(HttpMethod.Delete, $"/api/conversations/{p.Chat}/messages/{id}", cookie: p.CB)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var (_, n) = await Notice(p.CA, $"c={p.Chat}&m={id}");
        Assert.Equal("Tin nhắn đã được thu hồi", n.GetProperty("body").GetString());
        Assert.DoesNotContain("gửi nhầm", n.GetRawText());
    }

    [Fact]
    public async Task It_is_worded_in_the_persons_own_language()
    {
        var p = await Pair();
        await app.App(p.CA, "name");
        await using (var db = app.Db.NewContext())
            await db.Users.Where(u => u.Id == p.A.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, "en"));
        var id = await Say(p.CB, p.Chat, "chào");
        var (_, n) = await Notice(p.CA, $"c={p.Chat}&m={id}");
        Assert.Equal("New message", n.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Someone_outside_the_chat_or_a_hidden_message_gets_nothing()
    {
        var p = await Pair();
        var (_, stranger) = await app.Member();
        await app.App(stranger);
        var id = await Say(p.CB, p.Chat, "riêng tư");
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(stranger, $"c={p.Chat}&m={id}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(p.CA, $"c={p.Chat}&m={Guid.NewGuid()}")).Status);
        using (var r = await app.Call(HttpMethod.Put, $"/api/conversations/{p.Chat}/messages/{id}/hidden", cookie: p.CA)) Assert.True(r.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(p.CA, $"c={p.Chat}&m={id}")).Status);
    }

    [Fact]
    public async Task A_message_asked_for_by_another_chats_id_is_not_found()
    {
        var p = await Pair();
        var q = await Pair();
        var id = await Say(q.CB, q.Chat, "nhà khác");
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(p.CA, $"c={p.Chat}&m={id}")).Status);
    }

    [Fact]
    public async Task Without_both_ids_or_an_owner_kind_it_is_a_bad_request()
    {
        var p = await Pair();
        var (status, body) = await Notice(p.CA, $"c={p.Chat}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_notice", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_owner_hears_who_asked_to_join_and_of_a_contact_message_and_nobody_else_does()
    {
        var (_, owner) = await app.Owner();
        await app.App(owner, "name");
        using (var open = await app.Call(HttpMethod.Put, "/api/sign-up-open", new { open = true }, owner)) Assert.Equal(HttpStatusCode.NoContent, open.StatusCode);
        var name = "Bi " + Guid.NewGuid().ToString("N")[..6];
        using (var r = await app.Call(HttpMethod.Post, "/api/sign-up", new { gomId = "bi_" + Guid.NewGuid().ToString("N")[..8], password = AppFixture.Password, displayName = name, note = "Con của cô Lan" }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var (_, join) = await Notice(owner, "owner=join");
        Assert.Equal(("Gom", $"{name} xin tham gia Gom", "/settings/requests", "join-requests"), Parts(join));
        var (_, contact) = await Notice(owner, "owner=contact");
        Assert.Equal(("Gom", "Có thư liên hệ mới", "/settings/contact", "contact"), Parts(contact));

        var (_, someone) = await app.Member();
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(someone, "owner=join")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(owner, "owner=other")).Status);
    }

    [Fact]
    public async Task The_settings_switch_sees_the_apps_registration_and_its_level()
    {
        var p = await Pair();
        await app.App(p.CA, "nothing");
        using var r = await app.Call(HttpMethod.Get, "/api/push", cookie: p.CA);
        var state = await AppFixture.Json(r);
        Assert.True(state.GetProperty("subscribed").GetBoolean());
        Assert.Equal("nothing", state.GetProperty("level").GetString());
    }

    private const string Offer = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n";

    /// <summary>A calls B, whose app is registered at <paramref name="level"/> (so B can take calls).</summary>
    private async Task<((User A, string CA, User B, string CB, Guid Chat) P, Guid Call)> Ringing(string level)
    {
        var p = await Pair();
        await app.App(p.CB, level);
        using var r = await app.Call(HttpMethod.Post, "/api/calls", new { conversationId = p.Chat, clientId = Guid.NewGuid(), sdp = Offer, v = Gom.Web.Chat.CallEndpoints.ProtocolVersion }, p.CA);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (p, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_call_ringing_names_the_caller_and_opens_the_chat_and_nothing_names_nobody()
    {
        var (p, call) = await Ringing("words");
        var (status, n) = await Notice(p.CB, $"call={call}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((p.A.DisplayName, "Cuộc gọi đến", $"/c/{p.Chat}", p.Chat.ToString()), Parts(n));

        var (hidden, hiddenCall) = await Ringing("nothing");
        var (_, h) = await Notice(hidden.CB, $"call={hiddenCall}");
        Assert.Equal(("Gom", "Cuộc gọi đến"), (h.GetProperty("title").GetString(), h.GetProperty("body").GetString()));
    }

    [Fact]
    public async Task A_call_the_caller_gave_up_on_is_a_missed_call()
    {
        var (p, call) = await Ringing("name");
        using (var r = await app.Call(HttpMethod.Post, $"/api/calls/{call}/hangup", null, p.CA)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var (status, n) = await Notice(p.CB, $"call={call}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((p.A.DisplayName, "Cuộc gọi nhỡ", $"/c/{p.Chat}", p.Chat.ToString()), Parts(n));
    }

    [Fact]
    public async Task A_call_is_worded_only_for_the_person_called_while_it_rings_or_once_missed()
    {
        var (p, call) = await Ringing("words");
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(p.CA, $"call={call}")).Status);   // the caller: nothing to notify
        var (_, cs) = await app.Member();
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(cs, $"call={call}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(p.CB, $"call={Guid.NewGuid()}")).Status);
        using (var r = await app.Call(HttpMethod.Post, $"/api/calls/{call}/decline", null, p.CB)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Notice(p.CB, $"call={call}")).Status);   // declined: not missed
    }
}
