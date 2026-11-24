using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsEnvironment("Testing"))
{
    var connectionString = builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Connection string 'Default' is missing");

    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
}

var stripeSecretKey = builder.Configuration["Stripe:SecretKey"];
var stripeWebhookSecret = builder.Configuration["Stripe:WebhookSecret"];

const int SpotCount = 20;
const int RatePerHourCents = 200;

var app = builder.Build();

// using (var scope = app.Services.CreateScope())
// {
//     var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
//     db.Database.Migrate();

//     if (!db.Spots.Any())
//     {
//         for (var i = 1; i <= SpotCount; i++)
//             db.Spots.Add(new ParkingSpot { Number = i, IsOccupied = false });
//         await db.SaveChangesAsync();
//     }
// }



using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if (!app.Environment.IsEnvironment("Testing"))
        db.Database.Migrate();
    else
        db.Database.EnsureCreated();

    if (!db.Spots.Any())
    {
        for (var i = 1; i <= SpotCount; i++)
            db.Spots.Add(new ParkingSpot { Number = i, IsOccupied = false });
        await db.SaveChangesAsync();
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

// يحرر أي موقف انتهت مدته، ويرجّع قائمة المواقف محدّثة
async Task ReleaseExpiredSpots(AppDbContext db)
{
    var now = DateTime.UtcNow;
    var expired = await db.Sessions
        .Where(s => s.Status == SessionStatus.Active && s.EndTime <= now)
        .ToListAsync();

    foreach (var session in expired)
    {
        session.Status = SessionStatus.Completed;
        var spot = await db.Spots.FindAsync(session.SpotId);
        if (spot is not null) spot.IsOccupied = false;
    }

    if (expired.Count > 0) await db.SaveChangesAsync();
}

app.MapGet("/api/spots", async (AppDbContext db) =>
{
    await ReleaseExpiredSpots(db);

    var spots = await db.Spots.OrderBy(s => s.Number).ToListAsync();
    var activeSessions = await db.Sessions
        .Where(s => s.Status == SessionStatus.Active)
        .ToListAsync();

    var result = spots.Select(spot =>
    {
        var session = activeSessions.FirstOrDefault(s => s.SpotId == spot.Id);
        return new
        {
            spot.Id,
            spot.Number,
            spot.IsOccupied,
            EndTime = session?.EndTime
        };
    });

    return Results.Ok(result);
});

app.MapPost("/api/reserve", async (ReserveRequest input, HttpRequest request, AppDbContext db) =>
{
    if (input.Hours <= 0 || input.Hours > 24)
        return Results.BadRequest("Hours must be between 0 and 24");

    var spot = await db.Spots.FindAsync(input.SpotId);
    if (spot is null) return Results.NotFound("Spot not found");
    if (spot.IsOccupied) return Results.Conflict("Spot is already occupied");

    var amountCents = (int)Math.Ceiling(input.Hours * RatePerHourCents);

    var session = new ParkingSession
    {
        SpotId = spot.Id,
        Hours = input.Hours,
        AmountCents = amountCents,
        Status = SessionStatus.PendingPayment
    };
    db.Sessions.Add(session);
    await db.SaveChangesAsync();

    StripeConfiguration.ApiKey = stripeSecretKey;
    var baseUrl = $"{request.Scheme}://{request.Host}";

    var options = new SessionCreateOptions
    {
        Mode = "payment",
        LineItems = new List<SessionLineItemOptions>
        {
            new()
            {
                Quantity = 1,
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = "usd",
                    UnitAmount = amountCents,
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = $"Parking - Spot {spot.Number} - {input.Hours}h"
                    }
                }
            }
        },
        Metadata = new Dictionary<string, string>
        {
            { "parkingSessionId", session.Id.ToString() }
        },
        SuccessUrl = $"{baseUrl}/?paid=1",
        CancelUrl = $"{baseUrl}/?canceled=1"
    };

    var checkoutSession = await new SessionService().CreateAsync(options);
    return Results.Ok(new { url = checkoutSession.Url });
});

app.MapPost("/api/webhook/stripe", async (HttpRequest request, AppDbContext db) =>
{
    var json = await new StreamReader(request.Body).ReadToEndAsync();

    Event stripeEvent;
    try
    {
        stripeEvent = EventUtility.ConstructEvent(
            json,
            request.Headers["Stripe-Signature"],
            stripeWebhookSecret,
            throwOnApiVersionMismatch: false);
    }
    catch (StripeException ex)
    {
        Console.WriteLine($"Webhook signature verification failed: {ex.Message}");
        return Results.BadRequest();
    }

    if (stripeEvent.Type == EventTypes.CheckoutSessionCompleted)
    {
        var checkoutSession = stripeEvent.Data.Object as Stripe.Checkout.Session;
        var sessionIdText = checkoutSession?.Metadata is not null
            && checkoutSession.Metadata.TryGetValue("parkingSessionId", out var value)
            ? value
            : null;

        if (sessionIdText is not null && int.TryParse(sessionIdText, out var sessionId))
        {
            var session = await db.Sessions.FindAsync(sessionId);
            if (session is not null && session.Status == SessionStatus.PendingPayment)
            {
                var now = DateTime.UtcNow;
                session.Status = SessionStatus.Active;
                session.StartTime = now;
                session.EndTime = now.AddHours(session.Hours);

                var spot = await db.Spots.FindAsync(session.SpotId);
                if (spot is not null) spot.IsOccupied = true;

                await db.SaveChangesAsync();
            }
        }
    }

    return Results.Ok();
});

app.Run();

record ReserveRequest(int SpotId, double Hours);

public class ParkingSpot
{
    public int Id { get; set; }
    public int Number { get; set; }
    public bool IsOccupied { get; set; }
}

public enum SessionStatus { PendingPayment, Active, Completed }

public class ParkingSession
{
    public int Id { get; set; }
    public int SpotId { get; set; }
    public double Hours { get; set; }
    public int AmountCents { get; set; }
    public SessionStatus Status { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}


public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ParkingSpot> Spots => Set<ParkingSpot>();
    public DbSet<ParkingSession> Sessions => Set<ParkingSession>();
}