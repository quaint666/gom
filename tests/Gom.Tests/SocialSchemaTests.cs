using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>The database refuses what the rules forbid, whatever the code does.</summary>
public class SocialSchemaTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private async Task<User> NewUser()
    {
        await using var db = database.NewContext();
        var id = "s_" + Guid.NewGuid().ToString("N")[..10];
        var u = new User { Id = Guid.NewGuid(), GomId = id, DisplayName = id, PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static Friendship Pair(User a, User b, FriendStatus status = FriendStatus.Pending, Guid? requestedBy = null)
    {
        var (low, high) = Friendship.Order(a.Id, b.Id);
        return new Friendship
        {
            UserLow = low, UserHigh = high, RequestedBy = requestedBy ?? a.Id, Status = status, CreatedAt = DateTimeOffset.UtcNow,
            AcceptedAt = status == FriendStatus.Accepted ? DateTimeOffset.UtcNow : null,
        };
    }

    private async Task Refused(params object[] rows)
    {
        await using var db = database.NewContext();
        db.AddRange(rows);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_pair_order_in_code_is_the_order_Postgres_gives_uuids()
    {
        await using var db = database.NewContext();
        var conn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();
        for (var i = 0; i < 200; i++)
        {
            var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT @a < @b", conn);
            cmd.Parameters.AddWithValue("a", a);
            cmd.Parameters.AddWithValue("b", b);
            var postgresSaysAFirst = (bool)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(postgresSaysAFirst ? (a, b) : (b, a), Friendship.Order(a, b));
        }
    }

    [Fact]
    public async Task A_friendship_round_trips_and_a_pair_is_one_row()
    {
        var (a, b) = (await NewUser(), await NewUser());
        await using (var db = database.NewContext()) { db.Friendships.Add(Pair(a, b)); await db.SaveChangesAsync(); }

        await Refused(Pair(b, a));   // the same pair, asked from the other side
    }

    [Fact]
    public async Task A_friendship_stored_in_the_wrong_order_is_refused()
    {
        var (a, b) = (await NewUser(), await NewUser());
        var row = Pair(a, b);
        (row.UserLow, row.UserHigh) = (row.UserHigh, row.UserLow);
        await Refused(row);
    }

    [Fact]
    public async Task A_request_must_come_from_one_of_the_two()
    {
        var (a, b, c) = (await NewUser(), await NewUser(), await NewUser());
        await Refused(Pair(a, b, requestedBy: c.Id));
    }

    [Fact]
    public async Task Accepted_and_the_accepted_time_go_together()
    {
        var (a, b) = (await NewUser(), await NewUser());
        var accepted = Pair(a, b, FriendStatus.Accepted);
        accepted.AcceptedAt = null;
        var (c, d) = (await NewUser(), await NewUser());
        var pending = Pair(c, d);
        pending.AcceptedAt = DateTimeOffset.UtcNow;
        await Refused(accepted);
        await Refused(pending);
    }

    [Fact]
    public async Task Nobody_blocks_themself_or_blocks_the_same_person_twice()
    {
        var (a, b) = (await NewUser(), await NewUser());
        await Refused(new Block { BlockerId = a.Id, BlockedId = a.Id, CreatedAt = DateTimeOffset.UtcNow });
        await using (var db = database.NewContext()) { db.Blocks.Add(new Block { BlockerId = a.Id, BlockedId = b.Id, CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        await Refused(new Block { BlockerId = a.Id, BlockedId = b.Id, CreatedAt = DateTimeOffset.UtcNow });
    }

    private static Conversation Chat(User by, ConversationKind kind, string? key) =>
        new() { Id = Guid.NewGuid(), Kind = kind, DirectKey = key, CreatedBy = by.Id, CreatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public async Task A_direct_conversation_needs_a_key_and_a_group_must_not_have_one()
    {
        var a = await NewUser();
        await Refused(Chat(a, ConversationKind.Direct, null));
        await Refused(Chat(a, ConversationKind.Group, "x:y"));
    }

    [Fact]
    public async Task A_pair_has_one_direct_conversation()
    {
        var a = await NewUser();
        var key = Guid.NewGuid() + ":" + Guid.NewGuid();
        await using (var db = database.NewContext()) { db.Conversations.Add(Chat(a, ConversationKind.Direct, key)); await db.SaveChangesAsync(); }
        await Refused(Chat(a, ConversationKind.Direct, key));
    }

    [Fact]
    public async Task The_counter_never_goes_below_zero_and_a_member_is_listed_once()
    {
        var a = await NewUser();
        var chat = Chat(a, ConversationKind.Group, null);
        chat.LastSeq = -1;
        await Refused(chat);

        var ok = Chat(a, ConversationKind.Group, null);
        await using (var db = database.NewContext())
        {
            db.Conversations.Add(ok);
            db.Members.Add(new ConversationMember { ConversationId = ok.Id, UserId = a.Id, JoinedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await Refused(new ConversationMember { ConversationId = ok.Id, UserId = a.Id, JoinedAt = DateTimeOffset.UtcNow });
        await Refused(new ConversationMember { ConversationId = ok.Id, UserId = (await NewUser()).Id, JoinedAt = DateTimeOffset.UtcNow, LastReadSeq = -1 });
    }
}
