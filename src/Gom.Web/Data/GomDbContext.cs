using Gom.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Data;

public class GomDbContext(DbContextOptions<GomDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SiteSettings> Site => Set<SiteSettings>();
    public DbSet<ContactMessage> ContactMessages => Set<ContactMessage>();
    public DbSet<Call> Calls => Set<Call>();
    public DbSet<CallParty> CallParties => Set<CallParty>();
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
    public DbSet<FcmDevice> FcmDevices => Set<FcmDevice>();
    public DbSet<StickerPack> StickerPacks => Set<StickerPack>();
    public DbSet<CustomSticker> CustomStickers => Set<CustomSticker>();
    public DbSet<StickerPackChat> StickerPackChats => Set<StickerPackChat>();
    public DbSet<StickerPackPerson> StickerPackPeople => Set<StickerPackPerson>();

    /// <summary>
    /// The text without its accents ("Phở" is "Pho"), in the database: PostgreSQL's unaccent with its dictionary named, declared
    /// immutable so an index can be built on it (migration SearchIndex). Only for use inside a query.
    /// </summary>
    public static string Unaccent(string text) => throw new InvalidOperationException("Only inside a database query.");

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Search ignores accents ("pho" finds "Phở"). The extension ships with PostgreSQL's contrib package and is trusted, so the database's own owner can create it.
        b.HasPostgresExtension("unaccent");
        // Search finds words inside messages through a trigram index on gom_unaccent(body) (migration SearchIndex); trusted too.
        b.HasPostgresExtension("pg_trgm");
        b.HasDbFunction(typeof(GomDbContext).GetMethod(nameof(Unaccent))!).HasName("gom_unaccent");

        b.Entity<SiteSettings>(e =>
        {
            e.ToTable("site_settings", t => t.HasCheckConstraint("ck_site_settings_one_row", "id = 1"));
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasData(new SiteSettings { Id = 1, OpenSignUp = false });
        });

        b.Entity<Call>(e =>
        {
            e.ToTable("calls", t =>
            {
                t.HasCheckConstraint("ck_calls_state", "state IN (1, 2, 3)");
                t.HasCheckConstraint("ck_calls_outcome", "(state = 3) = (outcome IS NOT NULL) AND (outcome IS NULL OR outcome IN (1, 2, 3, 4, 5))");
                t.HasCheckConstraint("ck_calls_two", "caller_id <> callee_id");
            });
            e.HasIndex(x => new { x.CallerId, x.ClientId }).IsUnique();
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.MessageId);
            e.HasIndex(x => x.CalleeId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CallerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CalleeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CallParty>(e =>
        {
            e.ToTable("call_parties");
            e.HasKey(x => x.UserId);
            e.HasIndex(x => x.CallId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Call>().WithMany().HasForeignKey(x => x.CallId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ContactMessage>(e =>
        {
            e.ToTable("contact_messages");
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users", t =>
            {
                t.HasCheckConstraint("ck_users_gom_id", $"gom_id ~ '{GomId.DatabasePattern}'");
                // A deleted account is a disabled one, and never the owner.
                t.HasCheckConstraint("ck_users_deleted", "deleted_at IS NULL OR (disabled_at IS NOT NULL AND NOT is_owner)");
                // A request to join is never the owner; it is refused only while it is a request.
                t.HasCheckConstraint("ck_users_request", "requested_at IS NULL OR NOT is_owner");
                t.HasCheckConstraint("ck_users_refused", "refused_at IS NULL OR requested_at IS NOT NULL");
                t.HasCheckConstraint("ck_users_language", "language IN ('vi', 'en')");
            });
            e.Property(x => x.RequestNote).HasMaxLength(Auth.SignUpEndpoints.NoteMaxLength * 4);   // text elements; a flag is up to 4 UTF-16 units
            e.HasIndex(x => x.RequestedAt).HasFilter("requested_at IS NOT NULL");
            // Everyone who was here before languages existed reads Vietnamese: the database says so, not only the C# default.
            e.Property(x => x.Language).HasMaxLength(2).HasDefaultValue(ServerText.DefaultLanguage);
            e.Property(x => x.Palette).HasMaxLength(Chat.Appearance.CodeMaxLength).HasDefaultValue(Chat.Appearance.DefaultPalette);
            e.Property(x => x.Pattern).HasMaxLength(Chat.Appearance.CodeMaxLength).HasDefaultValue(Chat.Appearance.DefaultPattern);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.DeletedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.GomId).IsUnique();
            // At most one row with is_owner = true.
            e.HasIndex(x => x.IsOwner).IsUnique().HasFilter("is_owner");
            e.Property(x => x.DisplayName).HasMaxLength(Auth.DisplayName.MaxLength);
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
            e.ToTable("push_subscriptions", t => t.HasCheckConstraint("ck_push_level", "level IN ('name', 'nothing', 'words')"));
            e.HasIndex(x => x.SessionId).IsUnique();
            e.HasIndex(x => x.Endpoint).IsUnique();
            // A session is ended by marking it, not deleting it; were it ever deleted, its notifications go with it.
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Endpoint).HasMaxLength(1024);
            e.Property(x => x.Level).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<PushLevel>(v, true)).HasMaxLength(10);
        });

        b.Entity<FcmDevice>(e =>
        {
            e.ToTable("fcm_devices", t => t.HasCheckConstraint("ck_fcm_level", "level IN ('name', 'nothing', 'words')"));
            e.HasIndex(x => x.SessionId).IsUnique();
            e.HasIndex(x => x.Token).IsUnique();
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Token).HasMaxLength(1024);
            e.Property(x => x.Level).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<PushLevel>(v, true)).HasMaxLength(10);
        });

        b.Entity<OneTimeLink>(e =>
        {
            e.ToTable("one_time_links", t =>
            {
                t.HasCheckConstraint("ck_links_kind", "kind IN ('invite', 'reset')");
                t.HasCheckConstraint("ck_links_target", "(kind = 'reset') = (target_user_id IS NOT NULL)");
                t.HasCheckConstraint("ck_links_uses", "uses >= 0 AND (max_uses IS NULL OR (max_uses >= 1 AND uses <= max_uses))");
                t.HasCheckConstraint("ck_links_group", "group_id IS NULL OR kind = 'invite'");
            });
            e.Property(x => x.Kind).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<LinkKind>(v, true)).HasMaxLength(10);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ConsumedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
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
                t.HasCheckConstraint("ck_conversations_kind", "kind IN ('direct', 'group', 'announce')");
                t.HasCheckConstraint("ck_conversations_direct_key", "(kind = 'direct') = (direct_key IS NOT NULL)");
                t.HasCheckConstraint("ck_conversations_last_seq", "last_seq >= 0");
            });
            e.Property(x => x.Kind).HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<ConversationKind>(v, true)).HasMaxLength(10);
            e.Property(x => x.DirectKey).HasMaxLength(73);
            e.Property(x => x.Title).HasMaxLength(Auth.DisplayName.MaxLength);
            e.Property(x => x.Palette).HasMaxLength(Chat.Appearance.CodeMaxLength);
            e.Property(x => x.Pattern).HasMaxLength(Chat.Appearance.CodeMaxLength);
            e.HasIndex(x => x.DirectKey).IsUnique();
            e.HasIndex(x => x.Kind).IsUnique().HasFilter("kind = 'announce'");   // one announcement channel
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
                t.HasCheckConstraint("ck_conversation_members_cleared", "cleared_seq >= 0");
            });
            e.Property(x => x.MarkedUnread).HasDefaultValue(false);
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Role).HasDefaultValue(GroupRole.Member);
            e.Property(x => x.Muted).HasDefaultValue(false);
            // At most one leader per conversation.
            e.HasIndex(x => x.ConversationId).IsUnique().HasFilter("role = 2");
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StickerPack>(e =>
        {
            e.ToTable("sticker_packs");
            e.Property(x => x.Name).HasMaxLength(Chat.StickerPacks.NameChars);
            e.Property(x => x.Everywhere).HasDefaultValue(true).HasSentinel(true);
        });
        b.Entity<StickerPackChat>(e =>
        {
            e.ToTable("sticker_pack_chats");
            e.HasKey(x => new { x.PackId, x.ConversationId });
            e.HasOne<StickerPack>().WithMany().HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ConversationId);
        });
        b.Entity<StickerPackPerson>(e =>
        {
            e.ToTable("sticker_pack_people");
            e.HasKey(x => new { x.PackId, x.UserId });
            e.HasOne<StickerPack>().WithMany().HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.UserId);
        });
        b.Entity<CustomSticker>(e =>
        {
            e.ToTable("custom_stickers");
            e.Property(x => x.ContentType).HasMaxLength(20);
            e.HasOne<StickerPack>().WithMany().HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.PackId, x.Position });
        });
        b.Entity<Message>(e =>
        {
            e.ToTable("messages", t =>
            {
                t.HasCheckConstraint("ck_messages_seq", "seq > 0");
                t.HasCheckConstraint("ck_messages_notice", "notice IS NULL OR notice IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10)");
                t.HasCheckConstraint("ck_messages_notice_target", "(notice IS NOT NULL AND notice IN (1, 2)) = (notice_target_id IS NOT NULL)");
                t.HasCheckConstraint("ck_messages_sticker", "sticker IS NULL OR (body = '' AND notice IS NULL)");
            });
            e.Property(x => x.Body).HasMaxLength(16000);
            e.Property(x => x.Sticker).HasMaxLength(Chat.Stickers.MaxCodeLength);
            // Constant defaults, so adding the columns to a table of millions of messages changes no row.
            e.Property(x => x.Mentions).HasDefaultValueSql("'{}'");
            e.Property(x => x.MentionsAll).HasDefaultValue(false);
            // Order inside a conversation, and "this device already sent this one".
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            // Disappearing messages: the job finds those that are due without a scan.
            e.HasIndex(x => x.ExpiresAt).HasFilter("expires_at IS NOT NULL");
            e.HasIndex(x => new { x.SenderId, x.ClientId }).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.NoticeTargetId).OnDelete(DeleteBehavior.Restrict);
            // ReplyToId is deliberately not a key: trimming old messages keeps newer answers to them (see Message.ReplyToId).
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
            e.HasIndex(x => new { x.UploaderId, x.CreatedAt });  // a person's uploads of the last day, for the daily limit
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
