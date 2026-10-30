using System.Net.Http.Json;

namespace Travel.Coordinator.Services;

public sealed record TxRequest(Guid TransactionId);
public sealed record VoteResponse(Guid TransactionId, string Vote);
public sealed record DecisionResponse(Guid TransactionId, string Status);

public sealed class ParticipantHttpClient(IHttpClientFactory factory)
{
    public async Task<bool> PrepareAsync(
        string participant, Guid transactionId, CancellationToken ct = default)
    {
        var client = factory.CreateClient(participant);
        using var response = await client.PostAsJsonAsync(
            "api/2pc/prepare", new TxRequest(transactionId), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<VoteResponse>(cancellationToken: ct);
        if (result is null || result.TransactionId != transactionId)
            throw new InvalidOperationException("Invalid PREPARE response");
        if (result.Vote.Equals("YES", StringComparison.OrdinalIgnoreCase)) return true;
        if (result.Vote.Equals("NO", StringComparison.OrdinalIgnoreCase)) return false;
        throw new InvalidOperationException("Invalid vote");
    }

    public Task CommitAsync(string participant, Guid transactionId, CancellationToken ct = default)
        => SendDecisionAsync(participant, transactionId, "commit", "COMMITTED", ct);

    public Task AbortAsync(string participant, Guid transactionId, CancellationToken ct = default)
        => SendDecisionAsync(participant, transactionId, "abort", "ABORTED", ct);

    private async Task SendDecisionAsync(
        string participant, Guid transactionId, string action,
        string expectedStatus, CancellationToken ct)
    {
        var client = factory.CreateClient(participant);
        using var response = await client.PostAsJsonAsync(
            $"api/2pc/{action}", new TxRequest(transactionId), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<DecisionResponse>(cancellationToken: ct);
        if (result is null || result.TransactionId != transactionId ||
            !result.Status.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid {action} ACK from {participant}");
    }
}
