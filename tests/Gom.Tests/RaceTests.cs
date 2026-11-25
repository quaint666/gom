using System.Net;
using System.Net.Http.Json;
using Gom.Web.Data;
using Gom.Web.Uploads;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>
/// Requests that arrive at the same moment. A race needs several requests at once, not two, to show up reliably; each of these was
/// seen failing on the published build before its fix.
/// </summary>
public class RaceTests(AppFixture app) : IClassFixture<AppFixture>
{
    [Fact]
    public async Task Editing_and_taking_back_one_message_at_the_same_moment_never_deadlocks()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            using var r = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = $"m{i}" }, ca);
            ids.Add((await AppFixture.Json(r)).GetProperty("id").GetGuid());
        }

        var edits = ids.Select(id => app.Call(HttpMethod.Patch, $"/api/conversations/{chat}/messages/{id}", new { body = "đã sửa" }, ca)).ToList();
        var takeBacks = ids.Select(id => app.Call(HttpMethod.Delete, $"/api/conversations/{chat}/messages/{id}", cookie: ca)).ToList();
        var answers = await Task.WhenAll(edits.Concat(takeBacks));
        try
        {
            // An edit that arrives after its message was taken back finds nothing to edit; a take-back always succeeds.
            Assert.All(answers.Take(ids.Count), r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.NoContent, HttpStatusCode.NotFound }));
            Assert.All(answers.Skip(ids.Count), r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        }
        finally { foreach (var r in answers) r.Dispose(); }

        // Every one was taken back in the end, whichever came first.
        await using var db = app.Db.NewContext();
        Assert.Equal(8, await db.Messages.CountAsync(m => ids.Contains(m.Id) && m.DeletedAt != null));
    }

    [Fact]
    public async Task Guesses_sent_together_meet_the_slowdown_like_guesses_sent_one_by_one()
    {
        var user = await app.AddUser();
        using var client = app.Client();
        Task<HttpResponseMessage> Guess(int i)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/session") { Content = JsonContent.Create(new { gomId = user.GomId, password = $"wrong guess {i}" }) };
            request.Headers.Add("Origin", AppFixture.Origin);
            request.Headers.Add("X-Test-Ip", "203.0.113.77");
            return client.SendAsync(request);
        }
        var answers = await Task.WhenAll(Enumerable.Range(0, 30).Select(Guess));
        try
        {
            Assert.Equal(3, answers.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Equal(27, answers.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        }
        finally { foreach (var r in answers) r.Dispose(); }
    }

    [Fact]
    public async Task The_cleaner_leaves_an_old_upload_that_a_send_is_taking_at_that_very_moment()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        using var sent = await app.Call(HttpMethod.Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "x" }, ca);
        var message = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();

        var store = new DiskBlobStore(app.UploadsDir);
        var upload = Guid.NewGuid();
        await store.WriteAsync(upload.ToString(), new byte[5], default);
        await using (var db = app.Db.NewContext())
        {
            db.Attachments.Add(new Attachment { Id = upload, ConversationId = chat, UploaderId = a.Id, Kind = AttachmentKind.File, Name = "x.txt",
                ContentType = "application/octet-stream", Size = 5, CreatedAt = app.Clock.Now - TimeSpan.FromHours(3) });
            await db.SaveChangesAsync();
        }

        // A send has taken it, as Send does, but has not committed yet.
        await using var sending = app.Db.NewContext();
        await using var tx = await sending.Database.BeginTransactionAsync();
        await sending.Attachments.Where(x => x.Id == upload && x.MessageId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.MessageId, message));

        await using var run = app.Db.NewContext();
        var cleaning = AttachmentCleaner.RunOnce(run, store, app.Clock.Now, TimeSpan.FromHours(1), default);
        await Task.Delay(300);   // the cleaner has looked, and found the row busy
        await tx.CommitAsync();
        Assert.Equal(0, await cleaning);

        await using var check = app.Db.NewContext();
        Assert.Equal(message, (await check.Attachments.SingleAsync(x => x.Id == upload)).MessageId);
        Assert.NotNull(store.OpenRead(upload.ToString()));
    }
}
