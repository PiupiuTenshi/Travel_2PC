using Travel.Coordinator.Services;

namespace Travel.Coordinator;

public static class DemoEndpoints
{
    public static void MapDemoEndpoints(this WebApplication app)
    {
        // The caller registers this entire surface only in Development.
        app.UseStaticFiles();
        app.MapGet("/", () => Results.Redirect("/demo/"));
        app.MapGet("/demo/", () => Results.File(
            Path.Combine(app.Environment.WebRootPath, "demo", "index.html"), "text/html; charset=utf-8"));

        var demo = app.MapGroup("/dev/demo");
        demo.MapGet("/state", (DemoFaultSwitches faults, RecoverySettings settings) =>
        {
            return Results.Ok(new { paused = faults.PauseNotifications, recovery = settings,
                requestTimeoutSeconds = 2 });
        });
        demo.MapGet("/transactions", async (CoordinatorService service, CancellationToken ct) =>
        {
            var transactions = await service.GetRecentAsync(ct);
            return Results.Ok(transactions.Select(tx => new
            {
                transactionId = tx.Id, status = tx.Status.ToString(), tx.AirlineAck, tx.HotelAck,
                tx.RetryCount, tx.NextRetryAtUtc, tx.CreatedAtUtc, tx.ExpiresAtUtc
            }));
        });

    }
}
