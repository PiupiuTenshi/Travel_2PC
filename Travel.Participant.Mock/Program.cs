var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Mock participant: independent process, IN-MEMORY state, not crash durable.
var transactions = new Dictionary<Guid, string>();
var gate = new object();
var voteYes = true;
var commitMode = "OK";
var prepareDelayMs = 0;

app.MapPost("/api/2pc/prepare", async (TxRequest req) =>
{
    int delay;
    lock (gate) delay = prepareDelayMs;
    if (delay > 0) await Task.Delay(delay);

    lock (gate)
    {
        // IMPORTANT: ABORT might arrive while PREPARE is delayed. This check
        // prevents PREPARE from reviving an already-aborted transaction.
        if (transactions.TryGetValue(req.TransactionId, out var existing))
        {
            var oldVote = existing is "PREPARED" or "COMMITTED" ? "YES" : "NO";
            return Results.Ok(new { transactionId = req.TransactionId, vote = oldVote });
        }

        var currentVote = voteYes ? "YES" : "NO";
        transactions[req.TransactionId] = voteYes ? "PREPARED" : "ABORTED";
        Console.WriteLine($"PREPARE {req.TransactionId} => {currentVote}");
        return Results.Ok(new { transactionId = req.TransactionId, vote = currentVote });
    }
});

app.MapPost("/api/2pc/commit", (TxRequest req) =>
{
    lock (gate)
    {
        if (commitMode == "FAIL")
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (!transactions.TryGetValue(req.TransactionId, out var status) ||
            status is not ("PREPARED" or "COMMITTED"))
            return Results.Conflict(new { message = "Not prepared or was aborted" });

        // Idempotent: second COMMIT does not book twice.
        transactions[req.TransactionId] = "COMMITTED";
        Console.WriteLine($"COMMIT {req.TransactionId} => applied");

        if (commitMode == "APPLY_THEN_FAIL")
        {
            // Action is applied, but ACK is lost (simulated with HTTP 503).
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Ok(new { transactionId = req.TransactionId, status = "COMMITTED" });
    }
});

app.MapPost("/api/2pc/abort", (TxRequest req) =>
{
    lock (gate)
    {
        if (transactions.TryGetValue(req.TransactionId, out var status) &&
            status == "COMMITTED")
            return Results.Conflict(new { message = "Already committed" });
        transactions[req.TransactionId] = "ABORTED";
        Console.WriteLine($"ABORT {req.TransactionId} => ACK");
        return Results.Ok(new { transactionId = req.TransactionId, status = "ABORTED" });
    }
});

app.MapGet("/api/2pc/transactions/{id:guid}", (Guid id) =>
{
    lock (gate)
    {
        return transactions.TryGetValue(id, out var status)
            ? Results.Ok(new { transactionId = id, status })
            : Results.NotFound();
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapPost("/dev/vote/{value}", (string value) =>
    {
        var upper = value.ToUpperInvariant();
        if (upper is not ("YES" or "NO"))
            return Results.BadRequest(new { message = "Use YES or NO" });
        lock (gate) voteYes = upper == "YES";
        return Results.Ok(new { nextVote = upper });
    });

    app.MapPost("/dev/commit/{mode}", (string mode) =>
    {
        var upper = mode.ToUpperInvariant();
        if (upper is not ("OK" or "FAIL" or "APPLY_THEN_FAIL"))
            return Results.BadRequest(new { message = "Use OK, FAIL, APPLY_THEN_FAIL" });
        lock (gate) commitMode = upper;
        return Results.Ok(new { commitMode = upper });
    });

    app.MapPost("/dev/prepare-delay/{ms:int}", (int ms) =>
    {
        if (ms < 0 || ms > 10000)
            return Results.BadRequest(new { message = "ms must be 0..10000" });
        lock (gate) prepareDelayMs = ms;
        return Results.Ok(new { prepareDelayMs = ms });
    });
}

app.Run();

public sealed record TxRequest(Guid TransactionId);
