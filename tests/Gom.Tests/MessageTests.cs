using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class MessageTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get;

    private sealed record Pair(User A, string CookieA, User B, string CookieB, Guid Chat);

    private async Task<Pair> NewPair()
    {
        var (a, ca) = await app.Member();
        var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return new Pair(a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private Task<HttpResponseMessage> Send(string cookie, Guid chat, string body, Guid? clientId = null, string? origin = AppFixture.Origin) =>
        app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = clientId ?? Guid.NewGuid(), body }, cookie, origin);

    private async Task<long> LastSeq(Guid chat)
    {
        await using var db = app.Db.NewContext();
        return await db.Conversations.Where(c => c.Id == chat).Select(c => c.LastSeq).SingleAsync();
    }

    private async Task<int> MessageCount(Guid chat)
    {
        await using var db = app.Db.NewContext();
        return await db.Messages.CountAsync(m => m.ConversationId == chat);
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    /// <summary>Writes messages straight into the database, so a test can have a long history without sending hundreds.</summary>
    private async Task Seed(Guid chat, User sender, int count)
    {
        await using var db = app.Db.NewContext();
        var start = await db.Conversations.Where(c => c.Id == chat).Select(c => c.LastSeq).SingleAsync();
        for (var i = 1; i <= count; i++)
        {
            var m = new Message
            {
                Id = Guid.NewGuid(), ConversationId = chat, Seq = start + i, SenderId = sender.Id, ClientId = Guid.NewGuid(),
                Body = $"message {start + i}", CreatedAt = app.Clock.Now,
            };
            db.Messages.Add(m);
            db.Events.Add(new MessageEvent { ConversationId = chat, Seq = m.Seq, Kind = EventKind.Created, MessageId = m.Id, CreatedAt = m.CreatedAt });
        }
        await db.SaveChangesAsync();
        await db.Conversations.Where(c => c.Id == chat).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeq, start + count).SetProperty(c => c.LastMessageAt, app.Clock.Now));
    }

    // ---- sending

    [Fact]
    public async Task A_member_sends_and_everything_is_written_together()
    {
        var p = await NewPair();
        var client = Guid.NewGuid();

        using var response = await Send(p.CookieA, p.Chat, "xin chào", client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await AppFixture.Json(response);
        Assert.Equal(1, json.GetProperty("seq").GetInt64());
        Assert.Equal("xin chào", json.GetProperty("body").GetString());
        Assert.Equal(client, json.GetProperty("clientId").GetGuid());
        Assert.Equal(p.Chat, json.GetProperty("conversationId").GetGuid());
        Assert.Equal(p.A.GomId, json.GetProperty("sender").GetProperty("gomId").GetString());
        Assert.Equal(app.Clock.Now, json.GetProperty("createdAt").GetDateTimeOffset());

        await using var db = app.Db.NewContext();
        var message = await db.Messages.SingleAsync(m => m.ConversationId == p.Chat);
        Assert.Equal((json.GetProperty("id").GetGuid(), 1L, p.A.Id, client), (message.Id, message.Seq, message.SenderId, message.ClientId));
        var ev = await db.Events.SingleAsync(e => e.ConversationId == p.Chat);
        Assert.Equal((1L, EventKind.Created, message.Id), (ev.Seq, ev.Kind, ev.MessageId));
        var chat = await db.Conversations.SingleAsync(c => c.Id == p.Chat);
        Assert.Equal((1L, (DateTimeOffset?)app.Clock.Now), (chat.LastSeq, chat.LastMessageAt));
    }

    [Fact]
    public async Task Sequence_numbers_count_up_in_one_conversation_and_each_conversation_has_its_own()
    {
        var p = await NewPair();
        var (c, cc) = await app.Member();
        await app.Befriend(p.A, c);
        var other = await app.OpenChat(p.CookieA, c.GomId);

        var seqs = new List<long>();
        foreach (var (cookie, body) in new[] { (p.CookieA, "1"), (p.CookieB, "2"), (p.CookieA, "3") })
            seqs.Add((await AppFixture.Json(await Send(cookie, p.Chat, body))).GetProperty("seq").GetInt64());
        var first = (await AppFixture.Json(await Send(p.CookieA, other, "x"))).GetProperty("seq").GetInt64();

        Assert.Equal([1L, 2L, 3L], seqs);
        Assert.Equal(1L, first);
        Assert.Equal(3L, await LastSeq(p.Chat));
    }

    [Fact]
    public async Task Sending_moves_your_own_read_position_so_your_own_message_is_never_unread()
    {
        var p = await NewPair();
        using var sent = await Send(p.CookieA, p.Chat, "hi");

        await using var db = app.Db.NewContext();
        Assert.Equal(1L, await db.Members.Where(m => m.ConversationId == p.Chat && m.UserId == p.A.Id).Select(m => m.LastReadSeq).SingleAsync());
        Assert.Equal(0L, await db.Members.Where(m => m.ConversationId == p.Chat && m.UserId == p.B.Id).Select(m => m.LastReadSeq).SingleAsync());
    }

    [Fact]
    public async Task Someone_outside_the_conversation_gets_a_404_and_nothing_is_stored()
    {
        var p = await NewPair();
        var (_, outsider) = await app.Member();

        using var response = await Send(outsider, p.Chat, "let me in");
        using var unknown = await Send(p.CookieA, Guid.NewGuid(), "nowhere");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(0, await MessageCount(p.Chat));
        Assert.Equal(0L, await LastSeq(p.Chat));
    }

    [Fact]
    public async Task Sending_needs_a_session_and_a_good_Origin()
    {
        var p = await NewPair();

        using var anonymous = await app.Call(Post, $"/api/conversations/{p.Chat}/messages", new { clientId = Guid.NewGuid(), body = "x" });
        using var foreign = await Send(p.CookieA, p.Chat, "x", origin: "https://evil.test");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(0, await MessageCount(p.Chat));
    }

    [Fact]
    public async Task After_an_unfriend_or_a_block_nobody_can_send_in_the_old_conversation_but_it_stays_readable()
    {
        var p = await NewPair();
        using var first = await Send(p.CookieA, p.Chat, "before");
        using var unfriend = await app.Call(HttpMethod.Delete, $"/api/friends/{p.B.GomId}", cookie: p.CookieA);

        using var byA = await Send(p.CookieA, p.Chat, "after");
        using var byB = await Send(p.CookieB, p.Chat, "after");
        using var history = await app.Call(Get, $"/api/conversations/{p.Chat}/messages", cookie: p.CookieB);

        Assert.Equal(HttpStatusCode.Forbidden, byA.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byB.StatusCode);
        Assert.Equal("not_friends", await ErrorOf(byA));
        Assert.Equal(1, await MessageCount(p.Chat));
        Assert.Single((await AppFixture.Json(history)).GetProperty("messages").EnumerateArray());
    }

    // ---- what a message may contain

    [Fact]
    public async Task An_empty_or_blank_message_is_refused()
    {
        var p = await NewPair();
        foreach (var body in new[] { "", "   ", "\n\n", "\t" })
        {
            using var response = await Send(p.CookieA, p.Chat, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("empty_message", await ErrorOf(response));
        }
        using var missing = await app.Call(Post, $"/api/conversations/{p.Chat}/messages", new { clientId = Guid.NewGuid() }, p.CookieA);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(0, await MessageCount(p.Chat));
    }

    [Fact]
    public async Task Four_thousand_characters_are_fine_and_one_more_is_not()
    {
        var p = await NewPair();
        using var fine = await Send(p.CookieA, p.Chat, new string('a', 4000));
        using var tooLong = await Send(p.CookieA, p.Chat, new string('a', 4001));

        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("message_too_long", await ErrorOf(tooLong));
    }

    [Fact]
    public async Task A_character_is_what_a_person_sees_as_one_so_an_accented_letter_counts_once()
    {
        var p = await NewPair();
        var accented = "é";   // two code units, one character on screen

        using var fine = await Send(p.CookieA, p.Chat, string.Concat(Enumerable.Repeat(accented, 4000)));
        using var tooLong = await Send(p.CookieA, p.Chat, string.Concat(Enumerable.Repeat(accented, 4001)));

        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task One_character_with_a_endless_run_of_combining_marks_is_still_too_long()
    {
        var p = await NewPair();
        using var response = await Send(p.CookieA, p.Chat, "e" + new string('\u0301', 20000));   // one character on screen, 20,001 code units
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("message_too_long", await ErrorOf(response));
        Assert.Equal(0, await MessageCount(p.Chat));
    }

    [Fact]
    public async Task A_huge_body_is_refused_without_being_measured_character_by_character()
    {
        var p = await NewPair();
        using var response = await Send(p.CookieA, p.Chat, new string('a', 1_000_000));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("message_too_long", await ErrorOf(response));
    }

    [Fact]
    public async Task Line_breaks_and_emoji_are_kept_and_control_characters_are_refused()
    {
        var p = await NewPair();
        using var fine = await Send(p.CookieA, p.Chat, "dòng 1\r\ndòng 2\n👨‍👩‍👧‍👦\tok");
        using var bell = await Send(p.CookieA, p.Chat, "ring\u0007");
        using var nul = await Send(p.CookieA, p.Chat, "a\u0000b");
        using var lone = await Send(p.CookieA, p.Chat, "broken \ud83d surrogate");

        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        Assert.Equal("dòng 1\ndòng 2\n👨‍👩‍👧‍👦\tok", (await AppFixture.Json(fine)).GetProperty("body").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, bell.StatusCode);
        Assert.Equal("invalid_message", await ErrorOf(bell));
        Assert.Equal(HttpStatusCode.BadRequest, nul.StatusCode);
        // A half of an emoji cannot be stored. The JSON reader already turns it into the replacement character, so it never reaches the database.
        Assert.Equal(HttpStatusCode.OK, lone.StatusCode);
        Assert.Equal("broken \uFFFD surrogate", (await AppFixture.Json(lone)).GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_client_id_must_be_a_real_ID()
    {
        var p = await NewPair();
        foreach (var bad in new object?[] { "not-a-guid", "", null, "00000000-0000-0000-0000-000000000000" })
        {
            using var response = await app.Call(Post, $"/api/conversations/{p.Chat}/messages", new { clientId = bad, body = "x" }, p.CookieA);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_client_id", await ErrorOf(response));
        }
        Assert.Equal(0, await MessageCount(p.Chat));
    }

    // ---- retries

    [Fact]
    public async Task Sending_the_same_thing_twice_stores_it_once_and_answers_the_same()
    {
        var p = await NewPair();
        var client = Guid.NewGuid();

        using var first = await Send(p.CookieA, p.Chat, "once", client);
        using var again = await Send(p.CookieA, p.Chat, "once", client);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        Assert.Equal(1, await MessageCount(p.Chat));
        Assert.Equal(1L, await LastSeq(p.Chat));
    }

    [Fact]
    public async Task A_retry_gets_the_first_message_back_even_if_the_text_differs()
    {
        var p = await NewPair();
        var client = Guid.NewGuid();
        using var first = await Send(p.CookieA, p.Chat, "original", client);

        using var again = await Send(p.CookieA, p.Chat, "changed my mind", client);

        Assert.Equal("original", (await AppFixture.Json(again)).GetProperty("body").GetString());
        Assert.Equal(1, await MessageCount(p.Chat));
    }

    [Fact]
    public async Task A_retry_after_an_unfriend_still_returns_the_stored_message()
    {
        var p = await NewPair();
        var client = Guid.NewGuid();
        using var first = await Send(p.CookieA, p.Chat, "stored", client);
        using var unfriend = await app.Call(HttpMethod.Delete, $"/api/friends/{p.B.GomId}", cookie: p.CookieA);

        using var again = await Send(p.CookieA, p.Chat, "stored", client);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task A_client_id_used_in_another_conversation_is_a_409_and_stores_nothing_new()
    {
        var p = await NewPair();
        var (c, _) = await app.Member();
        await app.Befriend(p.A, c);
        var other = await app.OpenChat(p.CookieA, c.GomId);
        var client = Guid.NewGuid();
        using var first = await Send(p.CookieA, p.Chat, "here", client);

        using var clash = await Send(p.CookieA, other, "there", client);

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("client_id_reused", await ErrorOf(clash));
        Assert.Equal(0, await MessageCount(other));
        Assert.Equal(0L, await LastSeq(other));
    }

    [Fact]
    public async Task Two_people_may_use_the_same_client_id()
    {
        var p = await NewPair();
        var client = Guid.NewGuid();

        using var a = await Send(p.CookieA, p.Chat, "from a", client);
        using var b = await Send(p.CookieB, p.Chat, "from b", client);

        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.Equal(2, await MessageCount(p.Chat));
    }

    // ---- the engine under load

    [Fact]
    public async Task Thirty_sends_at_once_get_thirty_consecutive_numbers_with_no_gap_and_no_repeat()
    {
        var p = await NewPair();
        // Two people, all in flight together, each within the per-person limit of 20.
        var senders = new[] { p.CookieA, p.CookieB };
        var tasks = Enumerable.Range(0, 20).Select(i => Send(senders[i % 2], p.Chat, $"m{i}")).ToList();
        tasks.AddRange(Enumerable.Range(0, 10).Select(i => Send(p.CookieA, p.Chat, $"extra{i}")));

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var seqs = (await Task.WhenAll(responses.Select(AppFixture.Json))).Select(j => j.GetProperty("seq").GetInt64()).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), seqs);
        await using var db = app.Db.NewContext();
        Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), await db.Events.Where(e => e.ConversationId == p.Chat).OrderBy(e => e.Seq).Select(e => e.Seq).ToListAsync());
        Assert.Equal(30L, await LastSeq(p.Chat));
    }

    [Fact]
    public async Task Ten_copies_of_one_send_at_once_leave_one_message_and_one_number()
    {
        var p = await NewPair();
        var client = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Send(p.CookieA, p.Chat, "same", client)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = (await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))).Distinct().ToList();
        Assert.Single(bodies);
        Assert.Equal(1, await MessageCount(p.Chat));
        Assert.Equal(1L, await LastSeq(p.Chat));   // the losers' counter bumps were rolled back
        await using var db = app.Db.NewContext();
        Assert.Equal(1, await db.Events.CountAsync(e => e.ConversationId == p.Chat));
    }

    // ---- slowing a sender down

    [Fact]
    public async Task A_burst_of_twenty_is_allowed_then_a_wait_is_asked_for()
    {
        var p = await NewPair();
        for (var i = 0; i < 20; i++)
        {
            using var ok = await Send(p.CookieA, p.Chat, $"m{i}");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var slow = await Send(p.CookieA, p.Chat, "21st");

        Assert.Equal(HttpStatusCode.TooManyRequests, slow.StatusCode);
        Assert.Equal("too_many_messages", await ErrorOf(slow));
        Assert.True(int.Parse(slow.Headers.GetValues("Retry-After").Single()) >= 1);
        Assert.Equal(20, await MessageCount(p.Chat));
        using var other = await Send(p.CookieB, p.Chat, "someone else is unaffected");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Waiting_brings_sending_back_two_a_second_and_a_retry_costs_nothing()
    {
        var p = await NewPair();
        var client = Guid.NewGuid();
        for (var i = 0; i < 19; i++) { using var ok = await Send(p.CookieA, p.Chat, $"m{i}"); }
        using var last = await Send(p.CookieA, p.Chat, "20th", client);
        using var refused = await Send(p.CookieA, p.Chat, "21st");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        using var retry = await Send(p.CookieA, p.Chat, "20th", client);   // already stored: not a new send
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        using var one = await Send(p.CookieA, p.Chat, "after a second");
        using var two = await Send(p.CookieA, p.Chat, "second after a second");
        using var three = await Send(p.CookieA, p.Chat, "too soon");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, three.StatusCode);
    }

    // ---- history

    [Fact]
    public async Task History_gives_the_newest_page_newest_first_and_says_whether_there_is_more()
    {
        var p = await NewPair();
        await Seed(p.Chat, p.A, 120);

        using var response = await app.Call(Get, $"/api/conversations/{p.Chat}/messages", cookie: p.CookieB);

        var json = await AppFixture.Json(response);
        var seqs = json.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("seq").GetInt64()).ToArray();
        Assert.Equal(Enumerable.Range(71, 50).Reverse().Select(i => (long)i), seqs);
        Assert.True(json.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Asking_for_older_messages_continues_exactly_where_the_page_ended()
    {
        var p = await NewPair();
        await Seed(p.Chat, p.A, 120);

        using var page2 = await app.Call(Get, $"/api/conversations/{p.Chat}/messages?before=71", cookie: p.CookieB);
        using var page3 = await app.Call(Get, $"/api/conversations/{p.Chat}/messages?before=21", cookie: p.CookieB);

        var two = await AppFixture.Json(page2);
        Assert.Equal(Enumerable.Range(21, 50).Reverse().Select(i => (long)i), two.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("seq").GetInt64()));
        Assert.True(two.GetProperty("hasMore").GetBoolean());
        var three = await AppFixture.Json(page3);
        Assert.Equal(Enumerable.Range(1, 20).Reverse().Select(i => (long)i), three.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("seq").GetInt64()));
        Assert.False(three.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task The_page_size_can_be_chosen_but_never_above_a_hundred_or_below_one()
    {
        var p = await NewPair();
        await Seed(p.Chat, p.A, 130);

        async Task<int> Count(string query)
        {
            using var r = await app.Call(Get, $"/api/conversations/{p.Chat}/messages{query}", cookie: p.CookieB);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            return (await AppFixture.Json(r)).GetProperty("messages").GetArrayLength();
        }

        Assert.Equal(5, await Count("?limit=5"));
        Assert.Equal(100, await Count("?limit=1000"));
        Assert.Equal(1, await Count("?limit=0"));
        Assert.Equal(50, await Count("?limit=abc"));
    }

    [Fact]
    public async Task History_shows_who_sent_each_message_and_is_for_members_only()
    {
        var p = await NewPair();
        using var rename = await app.Call(HttpMethod.Patch, "/api/me", new { displayName = "Chị Hai" }, p.CookieA);
        using var sent = await Send(p.CookieA, p.Chat, "hello");
        var (_, outsider) = await app.Member();

        using var mine = await app.Call(Get, $"/api/conversations/{p.Chat}/messages", cookie: p.CookieB);
        using var theirs = await app.Call(Get, $"/api/conversations/{p.Chat}/messages", cookie: outsider);
        using var anonymous = await app.Call(Get, $"/api/conversations/{p.Chat}/messages");

        var message = (await AppFixture.Json(mine)).GetProperty("messages")[0];
        Assert.Equal(p.A.GomId, message.GetProperty("sender").GetProperty("gomId").GetString());
        Assert.Equal("Chị Hai", message.GetProperty("sender").GetProperty("displayName").GetString());
        Assert.Equal("hello", message.GetProperty("body").GetString());
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ---- reading and the list

    private async Task<JsonElement> ListEntry(string cookie, Guid chat)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == chat);
    }

    [Fact]
    public async Task The_list_shows_the_last_message_and_how_many_are_unread_by_you()
    {
        var p = await NewPair();
        using var m1 = await Send(p.CookieA, p.Chat, "first");
        using var m2 = await Send(p.CookieB, p.Chat, "reply");
        using var m3 = await Send(p.CookieA, p.Chat, "latest");

        var forB = await ListEntry(p.CookieB, p.Chat);
        var forA = await ListEntry(p.CookieA, p.Chat);

        Assert.Equal("latest", forB.GetProperty("lastMessage").GetProperty("body").GetString());
        Assert.Equal(p.A.GomId, forB.GetProperty("lastMessage").GetProperty("sender").GetProperty("gomId").GetString());
        Assert.Equal(3, forB.GetProperty("lastMessage").GetProperty("seq").GetInt64());
        Assert.Equal(1, forB.GetProperty("unread").GetInt32());    // B read up to their own reply (seq 2); only "latest" is new
        Assert.Equal(0, forA.GetProperty("unread").GetInt32());    // A sent the last one, and read the reply by sending after it
    }

    [Fact]
    public async Task Messages_I_did_not_send_count_as_unread_and_my_own_never_do()
    {
        var p = await NewPair();
        await Seed(p.Chat, p.B, 3);   // all from B, and nobody has read anything

        Assert.Equal(0, (await ListEntry(p.CookieB, p.Chat)).GetProperty("unread").GetInt32());
        Assert.Equal(3, (await ListEntry(p.CookieA, p.Chat)).GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task A_conversation_with_no_messages_has_no_last_message_and_nothing_unread()
    {
        var p = await NewPair();
        var entry = await ListEntry(p.CookieA, p.Chat);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("lastMessage").ValueKind);
        Assert.Equal(0, entry.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task A_long_last_message_is_cut_in_the_list_but_not_in_history()
    {
        var p = await NewPair();
        using var sent = await Send(p.CookieA, p.Chat, new string('x', 1000));

        var entry = await ListEntry(p.CookieB, p.Chat);
        using var history = await app.Call(Get, $"/api/conversations/{p.Chat}/messages", cookie: p.CookieB);

        Assert.Equal(200, entry.GetProperty("lastMessage").GetProperty("body").GetString()!.Length);
        Assert.Equal(1000, (await AppFixture.Json(history)).GetProperty("messages")[0].GetProperty("body").GetString()!.Length);
    }

    [Fact]
    public async Task The_list_puts_the_conversation_with_the_newest_message_first()
    {
        var (a, ca) = await app.Member();
        var (b, _) = await app.Member();
        var (c, _) = await app.Member();
        await app.Befriend(a, b);
        await app.Befriend(a, c);
        var withB = await app.OpenChat(ca, b.GomId);
        var withC = await app.OpenChat(ca, c.GomId);
        using var m1 = await Send(ca, withB, "older");
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        using var m2 = await Send(ca, withC, "newer");

        using var r = await app.Call(Get, "/api/conversations", cookie: ca);
        var ids = (await AppFixture.Json(r)).EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray();

        Assert.Equal([withC, withB], ids);
    }

    [Fact]
    public async Task Marking_read_moves_your_position_forward_never_back_and_never_past_the_end()
    {
        var p = await NewPair();
        for (var i = 0; i < 5; i++) { using var s = await Send(p.CookieA, p.Chat, $"m{i}"); app.Clock.Advance(TimeSpan.FromSeconds(1)); }

        async Task<long> ReadPosition(long? mark)
        {
            if (mark is not null)
            {
                using var r = await app.Call(Post, $"/api/conversations/{p.Chat}/read", new { seq = mark }, p.CookieB);
                Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
            }
            return (await ListEntry(p.CookieB, p.Chat)).GetProperty("lastReadSeq").GetInt64();
        }

        Assert.Equal(0L, await ReadPosition(null));
        Assert.Equal(3L, await ReadPosition(3));
        Assert.Equal(3L, await ReadPosition(1));      // never back
        Assert.Equal(5L, await ReadPosition(99));     // never past the newest
        Assert.Equal(0, (await ListEntry(p.CookieB, p.Chat)).GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task Marking_read_is_for_members_only_and_needs_a_good_Origin()
    {
        var p = await NewPair();
        var (_, outsider) = await app.Member();

        using var byOutsider = await app.Call(Post, $"/api/conversations/{p.Chat}/read", new { seq = 1 }, outsider);
        using var foreign = await app.Call(Post, $"/api/conversations/{p.Chat}/read", new { seq = 1 }, p.CookieA, origin: "https://evil.test");
        using var anonymous = await app.Call(Post, $"/api/conversations/{p.Chat}/read", new { seq = 1 });
        using var junk = await app.Call(Post, $"/api/conversations/{p.Chat}/read", new { seq = -4 }, p.CookieA);

        Assert.Equal(HttpStatusCode.NotFound, byOutsider.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
    }
}
