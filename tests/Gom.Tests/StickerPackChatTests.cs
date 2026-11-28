using System.Net;
using System.Text.Json;

namespace Gom.Tests;

/// <summary>
/// A sticker pack in every conversation, or only for the people and in the conversations the owner chose (H): the picker and every
/// send keep to it, and the owner has every pack.
/// </summary>
public class StickerPackChatTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put;

    private async Task<(Guid Pack, string Code)> PackWithOne()
    {
        var (_, owner) = await app.Owner();
        using var made = await app.Call(Post, "/api/owner/sticker-packs", new { name = "Chỉ nhà mình" }, owner);
        var pack = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        using var added = await app.CallBytes(Post, $"/api/owner/sticker-packs/{pack}/stickers", TestImages.Png(100, 100), owner);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        return (pack, (await AppFixture.Json(added)).GetProperty("code").GetString()!);
    }

    /// <summary>Two chats of the owner's, each with a new friend; returns the chats and the friends' cookies.</summary>
    private async Task<(Guid Chosen, string ChosenCookie, Guid Other, string OtherCookie)> TwoChats()
    {
        var (owner, cookie) = await app.Owner();
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(owner, a);
        await app.Befriend(owner, b);
        return (await app.OpenChat(cookie, a.GomId), ca, await app.OpenChat(cookie, b.GomId), cb);
    }

    private async Task<HttpResponseMessage> Choose(Guid pack, bool everywhere, params Guid[] chats) => await Where(pack, everywhere, chats, []);

    private async Task<HttpResponseMessage> Where(Guid pack, bool everywhere, Guid[] chats, string[] people)
    {
        var (_, owner) = await app.Owner();
        return await app.Call(Put, $"/api/owner/sticker-packs/{pack}/where", new { everywhere, chats, people }, owner);
    }

    private async Task<bool> InPicker(string cookie, Guid chat, Guid pack)
    {
        using var r = await app.Call(Get, $"/api/sticker-packs?conversation={chat}", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).EnumerateArray().Any(p => p.GetProperty("id").GetGuid() == pack);
    }

    private Task<HttpResponseMessage> Send(string cookie, Guid chat, string code) =>
        app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "", sticker = code }, cookie);

    [Fact]
    public async Task A_pack_for_chosen_chats_shows_and_sends_only_there()
    {
        var (pack, code) = await PackWithOne();
        var (chosen, cc, other, oc) = await TwoChats();
        Assert.True(await InPicker(oc, other, pack));   // a new pack is everywhere
        using (var r = await Choose(pack, false, chosen)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        Assert.True(await InPicker(cc, chosen, pack));
        Assert.False(await InPicker(oc, other, pack));
        using (var there = await Send(cc, chosen, code)) Assert.Equal(HttpStatusCode.OK, there.StatusCode);
        using (var elsewhere = await Send(oc, other, code)) Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);

        // Back to everywhere: both again.
        using (var r = await Choose(pack, true)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.True(await InPicker(oc, other, pack));
        using (var now = await Send(oc, other, code)) Assert.Equal(HttpStatusCode.OK, now.StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_sees_where_a_pack_shows_and_only_chats_the_owner_is_in_can_be_chosen()
    {
        var (pack, _) = await PackWithOne();
        var (chosen, cc, _, _) = await TwoChats();
        using (var r = await Choose(pack, false, chosen)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        var (_, owner) = await app.Owner();
        using (var mine = await app.Call(Get, "/api/sticker-packs", cookie: owner))
        {
            var row = (await AppFixture.Json(mine)).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == pack);
            Assert.False(row.GetProperty("everywhere").GetBoolean());
            Assert.Equal(chosen, Assert.Single(row.GetProperty("chats").EnumerateArray()).GetGuid());
        }
        using (var theirs = await app.Call(Get, "/api/sticker-packs", cookie: cc))
            Assert.DoesNotContain((await AppFixture.Json(theirs)).EnumerateArray(), p => p.GetProperty("id").GetGuid() == pack);

        // A chat between two others: the owner is not in it, so it cannot be chosen.
        var (x, cx) = await app.Member();
        var y = await app.AddUser();
        await app.Befriend(x, y);
        var theirsChat = await app.OpenChat(cx, y.GomId);
        using var refused = await Choose(pack, false, theirsChat);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Someone else asks about a chat they are not in: only what is everywhere.
        Assert.False(await InPicker(cx, chosen, pack));
    }

    [Fact]
    public async Task A_person_chosen_has_the_pack_in_every_chat_they_are_in_even_one_the_owner_cannot_see()
    {
        var (pack, code) = await PackWithOne();
        var (x, cx) = await app.Member();
        var (y, cy) = await app.Member();
        await app.Befriend(x, y);
        var theirs = await app.OpenChat(cx, y.GomId);
        using (var r = await Where(pack, false, [], [x.GomId])) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        Assert.True(await InPicker(cx, theirs, pack));
        Assert.False(await InPicker(cy, theirs, pack));   // the other one in the chat does not have it
        using (var sent = await Send(cx, theirs, code)) Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        using (var back = await Send(cy, theirs, code)) Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);

        var (_, owner) = await app.Owner();
        using var mine = await app.Call(Get, "/api/sticker-packs", cookie: owner);
        var row = (await AppFixture.Json(mine)).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == pack);
        Assert.Equal(x.GomId, Assert.Single(row.GetProperty("people").EnumerateArray()).GetString());

        // Taken off the list: no longer in their picker, nor sent.
        using (var r = await Where(pack, false, [], [])) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.False(await InPicker(cx, theirs, pack));
        using (var after = await Send(cx, theirs, code)) Assert.Equal(HttpStatusCode.BadRequest, after.StatusCode);
    }

    [Fact]
    public async Task The_owner_has_every_pack_wherever_they_are()
    {
        var (pack, code) = await PackWithOne();
        var (_, cookie) = await app.Owner();
        var (chosen, _, other, _) = await TwoChats();
        using (var r = await Choose(pack, false, chosen)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.True(await InPicker(cookie, other, pack));
        using var sent = await Send(cookie, other, code);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
    }

    [Fact]
    public async Task Only_members_still_using_Gom_can_be_chosen()
    {
        var (pack, _) = await PackWithOne();
        var locked = await app.AddUser(disabled: true);
        using var refused = await Where(pack, false, [], [locked.GomId]);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using var nobody = await Where(pack, false, [], ["no_such_person"]);
        Assert.Equal(HttpStatusCode.BadRequest, nobody.StatusCode);
    }
}
