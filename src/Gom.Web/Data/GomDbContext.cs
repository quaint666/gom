using Gom.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Data;

public class GomDbContext(DbContextOptions<GomDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<OneTimeLink> Links => Set<OneTimeLink>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> Members => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageEvent> Events => Set<MessageEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users", t => t.HasCheckConstraint("ck_users_gom_id", $"gom_id ~ '{GomId.DatabasePattern}'"));
            e.HasIndex(x => x.GomId).IsUnique();
            // At most one row with is_owner = true.
            e.HasIndex(x => x.IsOwner).IsUnique().HasFilter("is_owner");
            e.Property(x => x.DisplayName).HasMaxLength(60);
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.DeviceLabel).HasMaxLength(80);
        });

        b.Entity<OneTimeLink>(e =>
        {
            e.ToTable("one_time_links", t =>
            {
                t.HasCheckConstraint("ck_links_kind", "kind IN ('invite', 'reset')");
                t.HasCheckConstraint("ck_links_target", "(kind = 'reset') = (target_user_id IS NOT NULL)");
            });
            e.Property(x => x.Kind).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<LinkKind>(v, true)).HasMaxLength(10);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ConsumedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Friendship>(e =>
        {
            e.ToTable("friendships", t =>
            {
                t.HasCheckConstraint("ck_friendships_order", "user_low < user_high");
                t.HasCheckConstraint("ck_friendships_requester", "requested_by IN (user_low, user_high)");
                t.HasCheckConstraint("ck_friendships_status", "status IN ('pending', 'accepted')");
                t.HasCheckConstraint("ck_friendships_accepted", "(status = 'accepted') = (accepted_at IS NOT NULL)");
            });
            e.HasKey(x => new { x.UserLow, x.UserHigh });
            e.Property(x => x.Status).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<FriendStatus>(v, true)).HasMaxLength(10);
            e.HasIndex(x => x.UserHigh);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserLow).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserHigh).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Block>(e =>
        {
            e.ToTable("blocks", t => t.HasCheckConstraint("ck_blocks_not_self", "blocker_id <> blocked_id"));
            e.HasKey(x => new { x.BlockerId, x.BlockedId });
            e.HasIndex(x => x.BlockedId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.BlockerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.BlockedId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations", t =>
            {
                t.HasCheckConstraint("ck_conversations_kind", "kind IN ('direct', 'group')");
                t.HasCheckConstraint("ck_conversations_direct_key", "(kind = 'direct') = (direct_key IS NOT NULL)");
                t.HasCheckConstraint("ck_conversations_last_seq", "last_seq >= 0");
            });
            e.Property(x => x.Kind).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<ConversationKind>(v, true)).HasMaxLength(10);
            e.Property(x => x.DirectKey).HasMaxLength(73);
            e.Property(x => x.Title).HasMaxLength(60);
            e.HasIndex(x => x.DirectKey).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ConversationMember>(e =>
        {
            e.ToTable("conversation_members", t => t.HasCheckConstraint("ck_conversation_members_read", "last_read_seq >= 0"));
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages", t =>
            {
                t.HasCheckConstraint("ck_messages_seq", "seq > 0");
                t.HasCheckConstraint("ck_messages_body", "deleted_at IS NOT NULL OR length(body) > 0");
            });
            e.Property(x => x.Body).HasMaxLength(16000);
            // Order inside a conversation, and "this device already sent this one".
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.SenderId, x.ClientId }).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MessageEvent>(e =>
        {
            e.ToTable("message_events", t =>
            {
                t.HasCheckConstraint("ck_message_events_seq", "seq > 0");
                t.HasCheckConstraint("ck_message_events_kind", "kind IN ('created', 'deleted')");
            });
            e.HasKey(x => new { x.ConversationId, x.Seq });
            e.Property(x => x.Kind).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<EventKind>(v, true)).HasMaxLength(10);
            e.HasIndex(x => x.MessageId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        // Postgres convention: snake_case names, whatever the C# property is called.
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
                p.SetColumnName(Snake(p.Name));
            foreach (var k in entity.GetKeys()) k.SetName(Snake(k.GetName() ?? ""));
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName($"fk_{entity.GetTableName()}_{Snake(fk.Properties[0].Name)}");
            foreach (var ix in entity.GetIndexes())
                ix.SetDatabaseName($"ix_{entity.GetTableName()}_{string.Join('_', ix.Properties.Select(p => Snake(p.Name)))}");
        }
    }

    private static string Snake(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}
