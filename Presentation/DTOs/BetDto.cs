using Worldiator.Server.Domain;

namespace Worldiator.Server.Presentation.DTOs;

/// <summary>
/// Explicit bet projection for all client-facing messages.
/// Replaces anonymous objects scattered across GameHub, GameHostedService, and GameController.
/// Transaction IDs are intentionally excluded — clients never need those.
/// </summary>
public record BetDto(
    string BetId,
    string RoundId,
    string PlayerId,
    string Username,
    int SlotIndex,
    decimal Amount,
    string Currency,
    DateTime PlacedAt,
    decimal? AutoCashOutAt,
    decimal? CashedOutAt,
    decimal? WinAmount
)
{
    public static BetDto From(Bet bet) => new(
        bet.BetId, bet.RoundId, bet.PlayerId, bet.Username,
        bet.SlotIndex, bet.Amount, bet.Currency, bet.PlacedAt,
        bet.AutoCashOutAt, bet.CashedOutAt, bet.WinAmount
    );
}

/// <summary>Delta event sent when a new bet is placed — excludes cashout fields (not yet known).</summary>
public record BetAddedDto(
    string BetId,
    string RoundId,
    string PlayerId,
    string Username,
    int SlotIndex,
    decimal Amount,
    string Currency,
    DateTime PlacedAt,
    decimal? AutoCashOutAt
)
{
    public static BetAddedDto From(Bet bet) => new(
        bet.BetId, bet.RoundId, bet.PlayerId, bet.Username,
        bet.SlotIndex, bet.Amount, bet.Currency, bet.PlacedAt,
        bet.AutoCashOutAt
    );
}

/// <summary>Broadcast when any player cashes out (manual or auto).</summary>
public record BetCashedOutDto(string PlayerId, int SlotIndex, decimal CashedOutAt, decimal WinAmount)
{
    public static BetCashedOutDto From(Bet bet) => new(
        bet.PlayerId, bet.SlotIndex, bet.CashedOutAt!.Value, bet.WinAmount!.Value
    );
}
