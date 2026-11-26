using Brightline.Data.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Brightline.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options), IDataProtectionKeyContext
{
    public const string TicketNumberSequence = "ticket_number_seq";
    public const int FirstTicketNumber = 5310;
    public const int EmbeddingDimensions = 768;

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ToolCall> ToolCalls => Set<ToolCall>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasSequence<int>(TicketNumberSequence).StartsAt(FirstTicketNumber);
        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");

        builder.Entity<Source>(e =>
        {
            e.ToTable("sources");
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Uri).HasMaxLength(500);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.HasIndex(x => x.FileName).IsUnique();
        });

        builder.Entity<Chunk>(e =>
        {
            e.ToTable("chunks");
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.HeadingPath).HasMaxLength(500);
            e.HasIndex(x => new { x.SourceId, x.Version });
            e.HasIndex(x => x.IsActive);
            e.HasOne<Source>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.Property(x => x.Kind).HasMaxLength(60);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.RunAfter });
        });

        builder.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CustomerLabel).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(100);
            e.HasIndex(x => x.StartedAt);
            e.HasMany(x => x.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ToolCalls).WithOne().HasForeignKey(t => t.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Citations).HasColumnType("jsonb");
        });

        builder.Entity<ToolCall>(e =>
        {
            e.ToTable("tool_calls");
            e.Property(x => x.Tool).HasMaxLength(60);
            e.Property(x => x.CallId).HasMaxLength(100);
            e.Property(x => x.Args).HasColumnType("jsonb");
            e.Property(x => x.ResultStatus).HasMaxLength(40);
            e.Property(x => x.Approval).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200);
        });

        builder.Entity<Ticket>(e =>
        {
            e.ToTable("tickets");
            e.Property(x => x.Number).HasMaxLength(20).HasDefaultValueSql($"'TK-' || nextval('{TicketNumberSequence}')");
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Subject).HasMaxLength(200);
            e.Property(x => x.CustomerLabel).HasMaxLength(200);
            e.Property(x => x.OrderNumber).HasMaxLength(20);
            e.Property(x => x.ReturnNumber).HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.ConversationId);
        });

        builder.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.UpdatedBy).HasMaxLength(256);
        });

        SnakeCase(builder);
    }

    private static void SnakeCase(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is not null && table.StartsWith("AspNet", StringComparison.Ordinal))
            {
                continue; // keep Identity's well-known table and column names
            }

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    internal static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c)
            ? "_" + char.ToLowerInvariant(c)
            : char.ToLowerInvariant(c).ToString()));
}

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=brightline_app_design")
            .Options);
}
