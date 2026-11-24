using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>The message tables refuse what the sending rules forbid, whatever the code does.</summary>
public class MessageSchemaTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private async Task<(User User, Conversation Chat)> Setup()
    {
        await using var db = database.NewContext();
        var id = "m_" + Guid.NewGuid().ToString("N")[..10];
        var user = new User { Id = Guid.NewGuid(), GomId = id, DisplayName = id, PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow };
        var chat = new Conversation { Id = Guid.NewGuid(), Kind = ConversationKind.Group, CreatedBy = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        db.Conversations.Add(chat);
        await db.SaveChangesAsync();
        return (user, chat);
    }

    private static Message Msg(User by, Conversation chat, long seq, Guid? client = null, string body = "hello") => new()
    {
        Id = Guid.NewGuid(), ConversationId = chat.Id, Seq = seq, SenderId = by.Id, ClientId = client ?? Guid.NewGuid(), Body = body, CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task Save(params object[] rows)
    {
        await using var db = database.NewContext();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task Refused(params object[] rows)
    {
        await using var db = database.NewContext();
        db.AddRange(rows);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_message_round_trips()
    {
        var (user, chat) = await Setup();
        var message = Msg(user, chat, 1, body: "xin chào 👋");
        await Save(message);

        await using var db = database.NewContext();
        var back = await db.Messages.SingleAsync(m => m.Id == message.Id);
        Assert.Equal("xin chào 👋", back.Body);
        Assert.Null(back.DeletedAt);
    }

    [Fact]
    public async Task One_sequence_number_is_one_message_in_a_conversation_but_other_conversations_may_reuse_it()
    {
        var (user, chat) = await Setup();
        var (_, other) = await Setup();
        await Save(Msg(user, chat, 1));

        await Refused(Msg(user, chat, 1));
        await Save(Msg(user, other, 1));
    }

    [Fact]
    public async Task One_device_send_is_one_message_whatever_the_conversation()
    {
        var (user, chat) = await Setup();
        var (_, other) = await Setup();
        var client = Guid.NewGuid();
        await Save(Msg(user, chat, 1, client));

        await Refused(Msg(user, chat, 2, client));
        await Refused(Msg(user, other, 1, client));
    }

    [Fact]
    public async Task The_sequence_starts_at_one_and_an_empty_body_is_allowed_for_a_message_that_is_only_pictures()
    {
        var (user, chat) = await Setup();
        await Refused(Msg(user, chat, 0));
        await Refused(Msg(user, chat, -3));
        // Words may be empty when the message is only pictures or files; the API refuses a message with neither (see MessageAttachmentTests).
        await Save(Msg(user, chat, 5, body: ""));

        var recalled = Msg(user, chat, 6, body: "");
        recalled.DeletedAt = DateTimeOffset.UtcNow;
        await Save(recalled);
    }

    [Fact]
    public async Task An_event_is_one_step_of_one_conversation_and_needs_its_message()
    {
        var (user, chat) = await Setup();
        var message = Msg(user, chat, 1);
        await Save(message);
        await Save(new MessageEvent { ConversationId = chat.Id, Seq = 1, Kind = EventKind.Created, MessageId = message.Id, CreatedAt = DateTimeOffset.UtcNow });

        await Refused(new MessageEvent { ConversationId = chat.Id, Seq = 1, Kind = EventKind.Deleted, MessageId = message.Id, CreatedAt = DateTimeOffset.UtcNow });
        await Refused(new MessageEvent { ConversationId = chat.Id, Seq = 0, Kind = EventKind.Created, MessageId = message.Id, CreatedAt = DateTimeOffset.UtcNow });
        await Refused(new MessageEvent { ConversationId = chat.Id, Seq = 2, Kind = EventKind.Created, MessageId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow });
    }

    [Fact]
    public async Task Only_known_event_kinds_are_accepted()
    {
        var (user, chat) = await Setup();
        var message = Msg(user, chat, 1);
        await Save(message);
        await using var db = database.NewContext();
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO message_events (conversation_id, seq, kind, message_id, created_at) VALUES ({chat.Id}, 9, 'edited', {message.Id}, now())"));
        Assert.Equal("23514", ex.SqlState);   // check_violation
    }
}
