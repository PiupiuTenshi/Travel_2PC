using Microsoft.EntityFrameworkCore;
using Travel.Coordinator;
using Travel.Coordinator.Data;
using Travel.Coordinator.Models;
using Travel.Coordinator.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "coordinator.db");
Console.WriteLine($"Coordinator SQLite database: {databasePath}");

var settings = builder.Configuration.GetSection("Recovery").Get<RecoverySettings>()
               ?? new RecoverySettings();
if (settings.ActiveTtlSeconds <= 0 || settings.RetryScanSeconds <= 0 ||
    settings.CleanupScanSeconds <= 0 || settings.BatchSize <= 0)
    throw new InvalidOperationException("Recovery settings must be positive.");
builder.Services.AddSingleton(settings);

builder.Services.AddSingleton<SqliteDurabilityInterceptor>();
builder.Services.AddDbContextFactory<CoordinatorDbContext>((sp, options) =>
{
    options.UseSqlite($"Data Source={databasePath}");
    options.AddInterceptors(sp.GetRequiredService<SqliteDurabilityInterceptor>());
});

var airlineUrl = builder.Configuration["Participants:Airline"]
    ?? throw new InvalidOperationException("Missing Participants:Airline");
var hotelUrl = builder.Configuration["Participants:Hotel"]
    ?? throw new InvalidOperationException("Missing Participants:Hotel");

builder.Services.AddHttpClient("Airline", client =>
{
    client.BaseAddress = new Uri(airlineUrl);
    client.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddHttpClient("Hotel", client =>
{
    client.BaseAddress = new Uri(hotelUrl);
    client.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddSingleton<DemoFaultSwitches>();
builder.Services.AddSingleton<ParticipantHttpClient>();
builder.Services.AddSingleton<CoordinatorService>();
builder.Services.AddHostedService<RecoveryWorker>();
builder.Services.AddHostedService<TransactionCleanupWorker>();

var app = builder.Build();

// Demo-only schema creation: start with a NEW folder/database.
// EnsureCreated is NOT an EF Core migration mechanism.
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider
        .GetRequiredService<IDbContextFactory<CoordinatorDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

// Critical: reconcile abandoned PREPARING before accepting any HTTP requests.
// Single Coordinator process only; do NOT use this blindly for multiple replicas.
await app.Services.GetRequiredService<CoordinatorService>().RecoverOnStartupAsync();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Coordinator" }));

app.MapPost("/api/transactions", async (CoordinatorService service, CancellationToken ct) =>
{
    var tx = await service.BeginAsync(ct);
    return Results.Ok(ToResponse(tx));
});

app.MapGet("/api/transactions/{id:guid}",
    async (Guid id, CoordinatorService service, CancellationToken ct) =>
{
    var tx = await service.GetAsync(id, ct);
    return tx is null ? Results.NotFound() : Results.Ok(ToResponse(tx));
});

app.MapGet("/api/transactions/{id:guid}/logs",
    async (Guid id, CoordinatorService service, CancellationToken ct) =>
{
    if (await service.GetAsync(id, ct) is null) return Results.NotFound();
    var logs = await service.GetLogsAsync(id, ct);
    return Results.Ok(logs.Select(x => new
    {
        id = x.Id,
        transactionId = x.TransactionId,
        eventType = x.EventType,
        details = x.Details,
        createdAtUtc = x.CreatedAtUtc
    }));
});

app.MapPost("/api/transactions/{id:guid}/end",
    async (Guid id, CoordinatorService service) =>
{
    // Once 2PC starts, client disconnect should NOT cancel the global decision.
    var tx = await service.EndAsync(id);
    return tx is null ? Results.NotFound() : Results.Ok(ToResponse(tx));
});

app.MapPost("/api/transactions/{id:guid}/abort",
    async (Guid id, CoordinatorService service) =>
{
    var accepted = await service.AbortAsync(id);
    if (accepted is null) return Results.NotFound();
    if (accepted is false)
        return Results.Conflict(new { message = "Cannot ABORT: COMMIT was already decided." });
    var tx = (await service.GetAsync(id))!;
    return Results.Ok(ToResponse(tx));
});

// Fault injection is strictly for Development and does not perform real travel bookings.
if (app.Environment.IsDevelopment())
{
    app.MapDemoEndpoints();
    app.MapPost("/dev/notifications/{mode}", (string mode, DemoFaultSwitches faults) =>
    {
        var upper = mode.ToUpperInvariant();
        if (upper is not ("PAUSE" or "RESUME"))
            return Results.BadRequest(new { message = "Use PAUSE or RESUME" });
        faults.PauseNotifications = upper == "PAUSE";
        return Results.Ok(new { paused = faults.PauseNotifications });
    });

    // Makes an ACTIVE tx expire now, for fast and deterministic cleanup tests.
    app.MapPost("/dev/transactions/{id:guid}/expire",
        async (Guid id, CoordinatorService service) =>
    {
        var updated = await service.ForceExpireForDemoAsync(id);
        if (!updated) return Results.Conflict(new { message = "TX not found or not ACTIVE" });
        return Results.Ok(new { transactionId = id, expired = true });
    });

    // Writes PREPARING WITHOUT calling participants. Restart to test startup ABORT.
    app.MapPost("/dev/transactions/{id:guid}/simulate-preparing",
        async (Guid id, CoordinatorService service) =>
    {
        var updated = await service.ForcePreparingForDemoAsync(id);
        if (!updated) return Results.Conflict(new { message = "TX not found or not ACTIVE" });
        return Results.Ok(new { transactionId = id, status = "PREPARING" });
    });
}

app.Run();

static object ToResponse(TravelTransaction tx) => new
{
    transactionId = tx.Id,
    status = tx.Status.ToString(),
    airlineAck = tx.AirlineAck,
    hotelAck = tx.HotelAck,
    retryCount = tx.RetryCount,
    nextRetryAtUtc = tx.NextRetryAtUtc,
    createdAtUtc = tx.CreatedAtUtc,
    expiresAtUtc = tx.ExpiresAtUtc
};
