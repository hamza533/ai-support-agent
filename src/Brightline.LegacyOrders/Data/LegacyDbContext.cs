using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Brightline.LegacyOrders.Data;

/// <summary>The legacy system's own database. Only this API touches it.</summary>
public sealed class LegacyDbContext(DbContextOptions<LegacyDbContext> options) : DbContext(options)
{
    public const string ReturnNumberSequence = "return_number_seq";
    public const int FirstReturnNumber = 2291;

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<ReturnRequest> Returns => Set<ReturnRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<int>(ReturnNumberSequence).StartsAt(FirstReturnNumber);

        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.Property(x => x.Number).HasMaxLength(20);
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Total).HasPrecision(12, 2);
            e.HasOne(x => x.Customer).WithMany(c => c.Orders).HasForeignKey(x => x.CustomerId);
            e.HasOne(x => x.Delivery).WithOne().HasForeignKey<Delivery>(d => d.OrderId);
        });

        modelBuilder.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines");
            e.Property(x => x.Sku).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(60);
            e.Property(x => x.UnitPrice).HasPrecision(12, 2);
            e.HasOne(x => x.Order).WithMany(o => o.Lines).HasForeignKey(x => x.OrderId);
        });

        modelBuilder.Entity<Delivery>(e =>
        {
            e.ToTable("deliveries");
            e.Property(x => x.Carrier).HasMaxLength(60);
            e.Property(x => x.TrackingNumber).HasMaxLength(60);
        });

        modelBuilder.Entity<ReturnRequest>(e =>
        {
            e.ToTable("returns");
            e.Property(x => x.Number).HasMaxLength(20)
                .HasDefaultValueSql($"'RR-' || nextval('{ReturnNumberSequence}')");
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique();
            e.HasOne(x => x.OrderLine).WithMany(l => l.Returns).HasForeignKey(x => x.OrderLineId);
        });

        SnakeCaseColumns(modelBuilder);
    }

    private static void SnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
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

/// <summary>Lets <c>dotnet ef</c> build the model without a running database.</summary>
public sealed class LegacyDbContextFactory : IDesignTimeDbContextFactory<LegacyDbContext>
{
    public LegacyDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LegacyDbContext>()
            .UseNpgsql("Host=localhost;Database=brightline_legacy_design")
            .Options);
}
