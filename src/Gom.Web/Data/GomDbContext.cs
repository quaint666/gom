using Gom.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Gom.Web.Data;

public class GomDbContext(DbContextOptions<GomDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<OneTimeLink> Links => Set<OneTimeLink>();

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
