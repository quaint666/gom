using Gom.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Data;

public class GomDbContext(DbContextOptions<GomDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<OneTimeLink> Links => Set<OneTimeLink>();
    public DbSet<InviteUse> InviteUses => Set<InviteUse>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> Members => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageEvent> Events => Set<MessageEvent>();
    public DbSet<MessageHide> Hides => Set<MessageHide>();
    public DbSet<MessagePin> Pins => Set<MessagePin>();
    public DbSet<MessageReaction> Reactions => Set<MessageReaction>();
    public DbSet<JoinRequest> JoinRequests => Set<JoinRequest>();
    public DbSet<GroupBan> Bans => Set<GroupBan>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Search ignores accents ("pho" finds "Phở"). The extension ships with PostgreSQL's contrib package and is trusted, so the database's own owner can create it.
        b.HasPostgresExtension("unaccent");

        b.Entity<User>(e =>
        {
            e.ToTable("users", t => t.HasCheckConstraint("ck_users_gom_id", $"gom_id ~ '{GomId.DatabasePattern}'"));
            e.HasIndex(x => x.GomId).IsUnique();
            // At most one row with is_owner = true.
            e.HasIndex(x => x.IsOwner).IsUnique().HasFilter("is_owner");
            e.Property(x => x.DisplayName).HasMaxLength(60);
            // On for everyone who was here before the switch existed, too: the database says so, not only the C# default.
            e.Property(x => x.ShareReadReceipts).HasDefaultValue(true);
            // A picture is fetched by its own id: each belongs to one person.
            e.HasIndex(x => x.AvatarId).IsUnique();
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.DeviceLabel).HasMaxLength(80);
        });

        b.Entity<PushSubscription>(e =>
        {
            e.ToTable("push_subscriptions", t => t.HasCheckConstraint("ck_push_level", "level IN ('name', 'nothing')"));
            e.HasIndex(x => x.SessionId).IsUnique();
            e.HasIndex(x => x.Endpoint).IsUnique();
            // A session is ended by marking it, not deleting it; were it ever deleted, its notifications go with it.
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Endpoint).HasMaxLength(1024);
            e.Property(x => x.Level).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<PushLevel>(v, true)).HasMaxLength(10);
        });

        b.Entity<OneTimeLink>(e =>
        {
            e.ToTable("one_time_links", t =>
            {
                t.HasCheckConstraint("ck_links_kind", "kind IN ('invite', 'reset')");
                t.HasCheckConstraint("ck_links_target", "(kind = 'reset') = (target_user_id IS NOT NULL)");
                t.HasCheckConstraint("ck_links_uses", "uses >= 0 AND (max_uses IS NULL OR (max_uses >= 1 AND uses <= max_uses))");
            });
            e.Property(x => x.Kind).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<LinkKind>(v, true)).HasMaxLength(10);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ConsumedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<InviteUse>(e =>
        {
            e.ToTable("invite_uses");
            e.HasKey(x => new { x.LinkId, x.UserId });
            e.HasOne<OneTimeLink>().WithMany().HasForeignKey(x => x.LinkId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
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
            e.Property(x => x.JoinApproval).HasDefaultValue(false);
        });

        b.Entity<ConversationMember>(e =>
        {
            e.ToTable("conversation_members", t =>
            {
                t.HasCheckConstraint("ck_conversation_members_read", "last_read_seq >= 0");
                t.HasCheckConstraint("ck_conversation_members_joined", "joined_seq >= 0");
                t.HasCheckConstraint("ck_conversation_members_role", "role IN (0, 1, 2)");
            });
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Role).HasDefaultValue(GroupRole.Member);
            e.Property(x => x.Muted).HasDefaultValue(false);
            // At most one leader per conversation.
            e.HasIndex(x => x.ConversationId).IsUnique().HasFilter("role = 2");
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages", t =>
            {
                t.HasCheckConstraint("ck_messages_seq", "seq > 0");
                t.HasCheckConstraint("ck_messages_notice", "notice IS NULL OR notice IN (1, 2, 3, 4)");
                t.HasCheckConstraint("ck_messages_notice_target", "(notice IS NOT NULL AND notice IN (1, 2)) = (notice_target_id IS NOT NULL)");
            });
            e.Property(x => x.Body).HasMaxLength(16000);
            // Order inside a conversation, and "this device already sent this one".
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.SenderId, x.ClientId }).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.NoticeTargetId).OnDelete(DeleteBehavior.Restrict);
            // NoAction, not Restrict: ending a group deletes a message and its answers in one statement, and Restrict checks row by row.
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.ReplyToId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<MessageEvent>(e =>
        {
            e.ToTable("message_events", t =>
            {
                t.HasCheckConstraint("ck_message_events_seq", "seq > 0");
                t.HasCheckConstraint("ck_message_events_kind", "kind IN ('created', 'deleted', 'updated')");
            });
            e.HasKey(x => new { x.ConversationId, x.Seq });
            e.Property(x => x.Kind).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<EventKind>(v, true)).HasMaxLength(10);
            e.HasIndex(x => x.MessageId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MessageHide>(e =>
        {
            e.ToTable("message_hides");
            e.HasKey(x => new { x.UserId, x.MessageId });
            // Ending a group looks hides up by message.
            e.HasIndex(x => x.MessageId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Attachment>(e =>
        {
            e.ToTable("attachments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.HasIndex(x => x.ConversationId);
            e.HasIndex(x => x.MessageId);
            e.HasIndex(x => new { x.MessageId, x.CreatedAt });   // the cleanup of uploads nobody used
            e.ToTable(t => t.HasCheckConstraint("ck_attachments_kind", "kind in (0, 1, 2)"));
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UploaderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MessagePin>(e =>
        {
            e.ToTable("message_pins");
            e.HasKey(x => new { x.ConversationId, x.MessageId });
            e.HasIndex(x => x.MessageId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.PinnedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MessageReaction>(e =>
        {
            e.ToTable("message_reactions");
            e.HasKey(x => new { x.MessageId, x.UserId });
            e.Property(x => x.Emoji).HasMaxLength(16);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<JoinRequest>(e =>
        {
            e.ToTable("join_requests");
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<GroupBan>(e =>
        {
            e.ToTable("group_bans");
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.BannedBy).OnDelete(DeleteBehavior.Restrict);
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
