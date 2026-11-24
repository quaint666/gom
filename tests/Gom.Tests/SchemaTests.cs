using Gom.Web.Auth;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

public class SchemaTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private static User NewUser(string gomId) => new()
    {
        Id = Guid.NewGuid(), GomId = gomId, DisplayName = gomId, PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow,
    };

    private static OneTimeLink NewLink(LinkKind kind, Guid createdBy, Guid? target = null) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, TokenHash = Tokens.New().Hash, CreatedBy = createdBy,
        TargetUserId = target, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task A_user_round_trips()
    {
        await using var db = database.NewContext();
        var u = NewUser("round_trip");
        db.Users.Add(u);
        await db.SaveChangesAsync();

        await using var db2 = database.NewContext();
        var back = await db2.Users.SingleAsync(x => x.Id == u.Id);
        Assert.Equal("round_trip", back.GomId);
        Assert.False(back.IsOwner);
        Assert.Null(back.DisabledAt);
    }

    [Fact]
    public async Task Read_receipts_are_on_for_a_member_row_that_does_not_mention_them()
    {
        await using var db = database.NewContext();
        await db.Database.ExecuteSqlAsync($"INSERT INTO users (id, gom_id, display_name, password_hash, is_owner, created_at) VALUES ({Guid.NewGuid()}, 'old_member', 'old_member', 'x', false, now())");

        Assert.True(await db.Users.Where(u => u.GomId == "old_member").Select(u => u.ShareReadReceipts).SingleAsync());
    }

    [Fact]
    public async Task A_Gom_ID_must_be_unique()
    {
        await using var db = database.NewContext();
        db.Users.Add(NewUser("twin"));
        await db.SaveChangesAsync();

        await using var db2 = database.NewContext();
        db2.Users.Add(NewUser("twin"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Theory]
    [InlineData("Upper")]
    [InlineData("ab")]
    [InlineData("has space")]
    public async Task The_database_itself_refuses_a_malformed_Gom_ID(string gomId)
    {
        await using var db = database.NewContext();
        db.Users.Add(NewUser(gomId));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Only_one_owner_can_exist()
    {
        await using var db = database.NewContext();
        var first = NewUser("owner_one"); first.IsOwner = true;
        db.Users.Add(first);
        await db.SaveChangesAsync();

        await using var db2 = database.NewContext();
        var second = NewUser("owner_two"); second.IsOwner = true;
        db2.Users.Add(second);
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task A_session_token_hash_must_be_unique()
    {
        await using var db = database.NewContext();
        var u = NewUser("sess_user");
        db.Users.Add(u);
        var hash = Tokens.New().Hash;
        db.Sessions.Add(new Session { Id = Guid.NewGuid(), UserId = u.Id, TokenHash = hash, DeviceLabel = "Chrome", CreatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        await db.SaveChangesAsync();

        await using var db2 = database.NewContext();
        db2.Sessions.Add(new Session { Id = Guid.NewGuid(), UserId = u.Id, TokenHash = hash, DeviceLabel = "Chrome", CreatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }

    [Fact]
    public async Task A_session_needs_a_real_user()
    {
        await using var db = database.NewContext();
        db.Sessions.Add(new Session { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), TokenHash = Tokens.New().Hash, DeviceLabel = "x", CreatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task An_invite_has_no_target_and_a_reset_must_have_one()
    {
        await using var db = database.NewContext();
        var owner = NewUser("link_owner");
        db.Users.Add(owner);
        db.Links.Add(NewLink(LinkKind.Invite, owner.Id));
        await db.SaveChangesAsync();

        await using var bad1 = database.NewContext();
        bad1.Links.Add(NewLink(LinkKind.Reset, owner.Id, target: null));
        await Assert.ThrowsAsync<DbUpdateException>(() => bad1.SaveChangesAsync());

        await using var bad2 = database.NewContext();
        bad2.Links.Add(NewLink(LinkKind.Invite, owner.Id, target: owner.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => bad2.SaveChangesAsync());

        await using var ok = database.NewContext();
        ok.Links.Add(NewLink(LinkKind.Reset, owner.Id, target: owner.Id));
        await ok.SaveChangesAsync();
    }

    [Fact]
    public async Task A_link_kind_is_stored_as_readable_text()
    {
        await using var db = database.NewContext();
        var owner = NewUser("kind_owner");
        db.Users.Add(owner);
        db.Links.Add(NewLink(LinkKind.Invite, owner.Id));
        await db.SaveChangesAsync();

        var kind = await db.Database.SqlQuery<string>($"SELECT kind AS \"Value\" FROM one_time_links WHERE created_by = {owner.Id}").SingleAsync();
        Assert.Equal("invite", kind);
    }
}
