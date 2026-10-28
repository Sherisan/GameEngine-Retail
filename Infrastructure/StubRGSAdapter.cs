using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// Development/testing stub — simulates wallet operations in memory.
/// Replace with a real IRGSAdapter implementation for production.
/// </summary>
public class StubRGSAdapter : IRGSAdapter
{
    private readonly ILogger<StubRGSAdapter> _logger;
    private readonly decimal _seedBalance;
    // ConcurrentDictionary for thread-safe balance access under concurrent bet operations.
    private readonly ConcurrentDictionary<string, decimal> _balances = new();

    private const decimal DefaultBalance = 10000.00m;

    public StubRGSAdapter(ILogger<StubRGSAdapter> logger, IConfiguration config)
    {
        _logger = logger;
        _seedBalance = config.GetValue<decimal?>("RGS:Stub:DefaultBalance") ?? DefaultBalance;
    }

    public Task<RGSPlayerInfo?> AuthenticateAsync(string sessionToken)
    {
        _logger.LogWarning("[STUB RGS] AuthenticateAsync — using stub player");
        var playerId = $"stub_{sessionToken[..Math.Min(8, sessionToken.Length)]}";
        _balances.TryAdd(playerId, _seedBalance);
        return Task.FromResult<RGSPlayerInfo?>(new RGSPlayerInfo(
            PlayerId: playerId,
            Username: $"Player_{playerId[5..]}",
            Currency: "ZAR",
            SessionToken: sessionToken,
            Balance: _balances[playerId]
        ));
    }

    public Task<decimal> GetBalanceAsync(string playerId, string currency, string? gameId = null)
    {
        _logger.LogWarning("[STUB RGS] GetBalanceAsync — returning stub balance");
        return Task.FromResult(_balances.GetOrAdd(playerId, _seedBalance));
    }

    public Task<string> DebitAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        _logger.LogWarning("[STUB RGS] DebitAsync {PlayerId} {Amount} {Currency}", playerId, amount, currency);
        _balances.TryAdd(playerId, _seedBalance);

        // CAS loop — atomic check-and-deduct prevents double-spend under concurrent bets.
        while (true)
        {
            var current = _balances[playerId];
            if (current < amount)
                throw new RGSException("Insufficient funds", "INSUFFICIENT_FUNDS");
            if (_balances.TryUpdate(playerId, current - amount, current))
                return Task.FromResult($"STUB_DEBIT_{reference}");
        }
    }

    public Task<string> CreditAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
    {
        _logger.LogWarning("[STUB RGS] CreditAsync {PlayerId} {Amount} {Currency}", playerId, amount, currency);
        _balances.AddOrUpdate(playerId, amount, (_, existing) => existing + amount);
        return Task.FromResult($"STUB_CREDIT_{reference}");
    }

    public Task RollbackAsync(string playerId, string transactionId, string roundId, string? gameId = null, decimal? amount = null, string? currency = null)
    {
        _logger.LogWarning("[STUB RGS] RollbackAsync {PlayerId} tx:{TransactionId} amount:{Amount}", playerId, transactionId, amount);
        // Refund like the real wallet: exactly the amount passed. (Callers guard against refunding a debit twice.)
        if (amount is > 0)
            _balances.AddOrUpdate(playerId, amount.Value, (_, existing) => existing + amount.Value);
        return Task.CompletedTask;
    }
}
