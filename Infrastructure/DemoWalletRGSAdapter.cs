using Worldiator.Server.Contracts;

namespace Worldiator.Server.Infrastructure;

/// <summary>
/// Development/Staging only — routes demo players (minted by /api/dev-token, player id prefixed
/// "demo_") to the in-memory StubRGSAdapter wallet, and everyone else to the real operator wallet.
/// Lets the game be opened without a platform launch and still play with a demo balance,
/// while real launched players on the same deployment keep using the real wallet.
/// </summary>
public class DemoWalletRGSAdapter : IRGSAdapter
{
    public const string DemoPlayerPrefix = "demo_";

    private readonly IRGSAdapter _real;
    private readonly StubRGSAdapter _demo;

    public DemoWalletRGSAdapter(IRGSAdapter real, StubRGSAdapter demo)
    {
        _real = real;
        _demo = demo;
    }

    public static bool IsDemoPlayer(string playerId) => playerId.StartsWith(DemoPlayerPrefix, StringComparison.Ordinal);

    private IRGSAdapter For(string playerId) => IsDemoPlayer(playerId) ? _demo : _real;

    public Task<RGSPlayerInfo?> AuthenticateAsync(string sessionToken) => _real.AuthenticateAsync(sessionToken);

    public Task<decimal> GetBalanceAsync(string playerId, string currency, string? gameId = null)
        => For(playerId).GetBalanceAsync(playerId, currency, gameId);

    public Task<string> DebitAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
        => For(playerId).DebitAsync(playerId, amount, currency, roundId, reference, gameId);

    public Task<string> CreditAsync(string playerId, decimal amount, string currency, string roundId, string reference, string? gameId = null)
        => For(playerId).CreditAsync(playerId, amount, currency, roundId, reference, gameId);

    public Task RollbackAsync(string playerId, string transactionId, string roundId, string? gameId = null, decimal? amount = null, string? currency = null)
        => For(playerId).RollbackAsync(playerId, transactionId, roundId, gameId, amount, currency);
}
