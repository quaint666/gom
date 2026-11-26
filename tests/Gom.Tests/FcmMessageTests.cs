using System.Net;
using Gom.Web.Data;
using Gom.Web.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gom.Tests;

/// <summary>
/// Messages and the owner's notices reach the Android app by FCM too (docs/voice-calls.md, decisions 43 and 44): only which
/// conversation and message, never a name or words; the app fetches and words its own notification.
/// </summary>
public class FcmMessageTests(CallFixture app) : IClassFixture<CallFixture>
{
    private async Task<(User A, string CA, User B, string CB, Guid Chat)> Pair()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return (a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private async Task<Guid> Say(string cookie, Guid chat, string body = "xin chào")
    {
        using var r = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_message_wakes_the_other_persons_app_with_only_its_ids_and_never_the_writers()
    {
        var p = await Pair();
        var theirs = await app.App(p.CA);
        var mine = await app.App(p.CB);
        var id = await Say(p.CB, p.Chat, "hẹn 7 giờ nhé");
        var sent = await app.Fcm.Next(theirs);
        Assert.Equal(new Dictionary<string, string> { ["t"] = "message", ["c"] = p.Chat.ToString(), ["m"] = id.ToString() }, sent.Data);
        Assert.True(sent.High);
        Assert.Equal(2_419_200, sent.Ttl);
        await Task.Delay(200);
        Assert.Empty(app.Fcm.To(mine));
    }

    [Fact]
    public async Task A_muted_chat_wakes_no_app()
    {
        var p = await Pair();
        var theirs = await app.App(p.CA);
        await using (var db = app.Db.NewContext())
            await db.Members.Where(m => m.ConversationId == p.Chat && m.UserId == p.A.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Muted, true));
        await Say(p.CB, p.Chat);
        await Task.Delay(300);
        Assert.Empty(app.Fcm.To(theirs));
    }

    [Fact]
    public async Task A_message_taken_back_reaches_only_the_apps_that_show_words_at_normal_priority()
    {
        var p = await Pair();
        var words = await app.App(p.CA, "words");
        var name = await app.App(await app.SignIn(p.A.GomId, userAgent: "Tablet"), "name");
        var id = await Say(p.CB, p.Chat, "gửi nhầm");
        await app.Fcm.Next(words);
        await app.Fcm.Next(name);
        using (var r = await app.Call(HttpMethod.Delete, $"/api/conversations/{p.Chat}/messages/{id}", cookie: p.CB)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var recall = await app.Fcm.Next(words, 1);
        Assert.Equal(new Dictionary<string, string> { ["t"] = "recall", ["c"] = p.Chat.ToString(), ["m"] = id.ToString() }, recall.Data);
        Assert.False(recall.High);   // it shows nothing new, so it must not ask to wake the phone
        await Task.Delay(200);
        Assert.Single(app.Fcm.To(name));
    }

    [Fact]
    public async Task The_owners_app_hears_of_a_request_to_join_without_a_name()
    {
        var (_, owner) = await app.Owner();
        var phone = await app.App(owner);
        using (var open = await app.Call(HttpMethod.Put, "/api/sign-up-open", new { open = true }, owner)) Assert.Equal(HttpStatusCode.NoContent, open.StatusCode);
        using (var r = await app.Call(HttpMethod.Post, "/api/sign-up", new { gomId = "bi_" + Guid.NewGuid().ToString("N")[..8], password = AppFixture.Password, displayName = "Bi", note = "Con của cô Lan" }))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var sent = await app.Fcm.Next(phone);
        Assert.Equal(new Dictionary<string, string> { ["t"] = "owner", ["kind"] = "join" }, sent.Data);
        Assert.False(sent.High);
    }

    [Fact]
    public async Task A_missed_call_reaches_the_app_once_as_its_stop_ringing_push()
    {
        var p = await Pair();
        await app.App(p.CA);
        var theirs = await app.App(p.CB);
        using (var r = await app.Call(HttpMethod.Post, "/api/calls", new { conversationId = p.Chat, clientId = Guid.NewGuid(), sdp = "v=0\r\n", v = Gom.Web.Chat.CallEndpoints.ProtocolVersion }, p.CA))
        {
            var id = (await AppFixture.Json(r)).GetProperty("id").GetGuid();
            using (await app.Call(HttpMethod.Post, $"/api/calls/{id}/hangup", cookie: p.CA)) { }
        }
        Assert.Equal("missed", (await app.Fcm.Next(theirs, 1)).Data["outcome"]);
        await Task.Delay(300);
        Assert.Equal(["call", "call-end"], app.Fcm.To(theirs).Select(s => s.Data["t"]));
    }

    [Fact]
    public async Task An_app_fcm_says_is_gone_is_forgotten_when_a_message_finds_it()
    {
        var p = await Pair();
        var theirs = await app.App(p.CA);
        app.Fcm.Answers[theirs] = () => new FcmResult(404);
        await Say(p.CB, p.Chat);
        await app.Fcm.Next(theirs);
        await using var db = app.Db.NewContext();
        for (var i = 0; i < 50 && await db.FcmDevices.AnyAsync(d => d.Token == theirs); i++) await Task.Delay(20);
        Assert.False(await db.FcmDevices.AnyAsync(d => d.Token == theirs));
    }

    [Fact]
    public async Task With_only_an_fcm_account_set_the_apps_are_still_told_and_no_browser_is_tried()
    {
        var p = await Pair();
        var theirs = await app.App(p.CA);
        var id = await Say(p.CB, p.Chat);
        await app.Fcm.Next(theirs);
        var account = FcmAccount.Parse(CallFixture.FcmAccount);
        var noKeys = Options.Create(new PushOptions());
        var queue = new PushQueue(noKeys, account, NullLogger<PushQueue>.Instance);
        Assert.True(queue.Enabled);
        var browsers = new FakePush();
        var sender = new PushSender(queue, app.Factory.Services.GetRequiredService<IServiceScopeFactory>(), browsers, noKeys, app.Fcm, account, app.Clock,
            NullLogger<PushSender>.Instance);
        long seq;
        await using (var db = app.Db.NewContext()) seq = await db.Messages.Where(m => m.Id == id).Select(m => m.Seq).SingleAsync();
        await sender.DeliverAsync(new PushJob(p.Chat, seq, p.B.Id, p.B.DisplayName, 0, 0, Text: "xin chào", MessageId: id), CancellationToken.None);
        Assert.Equal(2, app.Fcm.To(theirs).Count);
        Assert.Empty(browsers.All);
    }
}
