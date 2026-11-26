using Microsoft.EntityFrameworkCore;

namespace Brightline.LegacyOrders.Data;

/// <summary>
/// Deterministic synthetic seed data (~50 customers, ~150 orders). Dates are relative to "now" so the demo story
/// (Maya Thompson, order BL-10482 delivered 9 days ago) stays true after every nightly reset.
/// </summary>
public sealed class LegacySeeder(LegacyDbContext db, TimeProvider time, ILogger<LegacySeeder> logger)
{
    public const string MayaEmail = "maya.t@example.com";
    public const string MayaOrder = "BL-10482";
    public const string MayaOldOrder = "BL-10317";
    public const string DanielEmail = "daniel.o@example.com";
    public const string DanielOrder = "BL-10555";

    private static readonly (string Sku, string Name, string Category, decimal Price)[] _catalog =
    [
        ("AUD-AURORA-WH", "Aurora Wireless Headphones", "Audio", 149m),
        ("AUD-PULSE-EB", "Pulse Earbuds Pro", "Audio", 99m),
        ("AUD-BOOM-SPK", "Boom Portable Speaker", "Audio", 79m),
        ("ACC-USBC-65W", "USB-C Charger 65W", "Accessories", 39m),
        ("ACC-CABLE-2M", "Braided USB-C Cable 2 m", "Accessories", 15m),
        ("ACC-SLEEVE-14", "Laptop Sleeve 14\"", "Accessories", 29m),
        ("LAP-NOVA-14", "Nova 14 Laptop", "Laptops", 1099m),
        ("LAP-NOVA-16", "Nova 16 Pro Laptop", "Laptops", 1749m),
        ("PHN-ORBIT-8", "Orbit 8 Smartphone", "Phones", 699m),
        ("PHN-ORBIT-8C", "Orbit 8 Clear Case", "Accessories", 19m),
        ("WEA-STRIDE-W", "Stride Smartwatch", "Wearables", 249m),
        ("WEA-FIT-BAND", "FitBand Lite", "Wearables", 59m),
        ("SMH-GLOW-BULB", "Glow Smart Bulb (2-pack)", "Smart Home", 25m),
        ("SMH-HUB-MINI", "Home Hub Mini", "Smart Home", 89m),
        ("SMH-CAM-IN", "Indoor Security Camera", "Smart Home", 69m),
        ("GAM-CTRL-X", "Vector Game Controller", "Gaming", 64m),
        ("GAM-HEADSET", "Vector Gaming Headset", "Gaming", 89m),
        ("MON-VIEW-27", "ViewPro 27\" Monitor", "Monitors", 329m),
        ("SFT-SHIELD-1Y", "Shield Antivirus (1-year licence)", "Software", 39m),
        ("GFT-CARD-50", "Brightline Gift Card $50", "Gift Cards", 50m),
    ];

    private static readonly string[] _firstNames =
    [
        "Liam", "Olivia", "Noah", "Emma", "Ava", "Elijah", "Sofia", "Lucas", "Isabella", "Mateo",
        "Mia", "Ethan", "Amelia", "James", "Harper", "Aiden", "Chloe", "Leo", "Zoe", "Arjun",
        "Priya", "Kenji", "Yuki", "Fatima", "Omar", "Grace", "Hannah", "Samuel", "Nora", "Ravi",
    ];

    private static readonly string[] _lastNames =
    [
        "Garcia", "Nguyen", "Patel", "Kim", "Smith", "Johnson", "Brown", "Rossi", "Müller", "Silva",
        "Khan", "Cohen", "Novak", "Larsen", "Adeyemi", "Tanaka", "Lopez", "Wilson", "Moreau", "Haddad",
    ];

    private static readonly string[] _carriers = ["ParcelLine", "SwiftShip", "NorthPost"];

    public async Task<bool> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        if (await db.Customers.AnyAsync(ct))
        {
            return false;
        }

        await SeedAsync(ct);
        return true;
    }

    /// <summary>Deletes all data, restarts identities and the return-number sequence, then seeds again.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE returns, deliveries, order_lines, orders, customers RESTART IDENTITY CASCADE", ct);
        await db.Database.ExecuteSqlRawAsync(
            $"ALTER SEQUENCE {LegacyDbContext.ReturnNumberSequence} RESTART WITH {LegacyDbContext.FirstReturnNumber}", ct);
        db.ChangeTracker.Clear();
        await SeedAsync(ct);
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rng = new Random(42);
        var customers = BuildCustomers(rng, now);
        db.Customers.AddRange(customers);

        var usedNumbers = new HashSet<string> { MayaOrder, MayaOldOrder, DanielOrder };

        // The demo story, fixed.
        var maya = customers[0];
        AddOrder(maya, MayaOrder, now.AddDays(-13), OrderStatus.Delivered, now.AddDays(-9), rng,
            [Item("AUD-AURORA-WH", 1), Item("ACC-USBC-65W", 1)]);
        AddOrder(maya, MayaOldOrder, now.AddDays(-50), OrderStatus.Delivered, now.AddDays(-45), rng,
            [Item("SMH-HUB-MINI", 1)]);
        var daniel = customers[1];
        AddOrder(daniel, DanielOrder, now.AddDays(-8), OrderStatus.Delivered, now.AddDays(-4), rng,
            [Item("LAP-NOVA-14", 1), Item("ACC-SLEEVE-14", 1)]);

        // ~147 random orders across all customers.
        for (var i = 0; i < 147; i++)
        {
            var customer = customers[rng.Next(customers.Count)];
            string number;
            do
            {
                number = $"BL-{rng.Next(10001, 11000)}";
            }
            while (!usedNumbers.Add(number));

            var placedDaysAgo = rng.Next(1, 120);
            var roll = rng.NextDouble();
            var status = roll switch
            {
                < 0.70 => OrderStatus.Delivered,
                < 0.85 => OrderStatus.Shipped,
                < 0.95 => OrderStatus.Placed,
                _ => OrderStatus.Cancelled,
            };
            if (placedDaysAgo < 4 && status == OrderStatus.Delivered)
            {
                status = OrderStatus.Shipped;
            }

            DateTimeOffset? deliveredAt = status == OrderStatus.Delivered
                ? now.AddDays(-placedDaysAgo + rng.Next(2, Math.Min(6, placedDaysAgo)))
                : null;

            var lineCount = rng.Next(1, 4);
            var items = Enumerable.Range(0, lineCount)
                .Select(_ => _catalog[rng.Next(_catalog.Length)])
                .DistinctBy(c => c.Sku)
                .Select(c => Item(c.Sku, c.Price > 500 ? 1 : rng.Next(1, 3)))
                .ToList();

            AddOrder(customer, number, now.AddDays(-placedDaysAgo), status, deliveredAt, rng, items);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Legacy seed complete: {Customers} customers, {Orders} orders",
            customers.Count, customers.Sum(c => c.Orders.Count));
    }

    private static List<Customer> BuildCustomers(Random rng, DateTimeOffset now)
    {
        var customers = new List<Customer>
        {
            new() { Name = "Maya Thompson", Email = MayaEmail, Phone = "+1 555 0100", CreatedAt = now.AddYears(-2) },
            new() { Name = "Daniel Okafor", Email = DanielEmail, Phone = "+1 555 0101", CreatedAt = now.AddYears(-1) },
        };
        var emails = customers.Select(c => c.Email).ToHashSet();
        while (customers.Count < 50)
        {
            var first = _firstNames[rng.Next(_firstNames.Length)];
            var last = _lastNames[rng.Next(_lastNames.Length)];
            var email = $"{first.ToLowerInvariant()}.{AsciiInitial(last)}{rng.Next(1, 99)}@example.com";
            if (!emails.Add(email))
            {
                continue;
            }

            customers.Add(new Customer
            {
                Name = $"{first} {last}",
                Email = email,
                Phone = $"+1 555 {rng.Next(1000, 9999)}",
                CreatedAt = now.AddDays(-rng.Next(30, 900)),
            });
        }

        return customers;
    }

    private static char AsciiInitial(string name) => char.ToLowerInvariant(name[0]) switch
    {
        'ü' => 'u',
        var c => c,
    };

    private static (string Sku, int Qty) Item(string sku, int qty) => (sku, qty);

    private static void AddOrder(
        Customer customer,
        string number,
        DateTimeOffset placedAt,
        OrderStatus status,
        DateTimeOffset? deliveredAt,
        Random rng,
        List<(string Sku, int Qty)> items)
    {
        var order = new Order { Number = number, PlacedAt = placedAt, Status = status };
        foreach (var (sku, qty) in items)
        {
            var product = _catalog.Single(c => c.Sku == sku);
            order.Lines.Add(new OrderLine
            {
                Sku = product.Sku,
                Name = product.Name,
                Category = product.Category,
                Quantity = qty,
                UnitPrice = product.Price,
            });
        }

        order.Total = order.Lines.Sum(l => l.UnitPrice * l.Quantity);
        if (status is OrderStatus.Shipped or OrderStatus.Delivered)
        {
            order.Delivery = new Delivery
            {
                Carrier = _carriers[rng.Next(_carriers.Length)],
                TrackingNumber = $"TRK{rng.Next(100000000, 999999999)}",
                ShippedAt = placedAt.AddDays(1),
                DeliveredAt = deliveredAt,
                EstimatedDelivery = DateOnly.FromDateTime((deliveredAt ?? placedAt.AddDays(5)).UtcDateTime),
            };
        }

        customer.Orders.Add(order);
    }
}
