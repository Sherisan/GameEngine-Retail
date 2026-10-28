using Worldiator.Server.Domain;

namespace Worldiator.Server.Presentation.DTOs;

/// <summary>Sent to a client that connects mid-round.</summary>
public record RoundStateDto(
    string RoundId,
    GamePhase Phase,
    string ServerSeedHash,
    string ClientSeed,
    long Nonce,
    IEnumerable<BetDto> Bets,
    /// <summary>
    /// Server-computed elapsed flying time in ms, as of the moment this DTO was built.
    /// Null unless Phase == Flying. The client should seed its local animation clock with
    /// (performance.now() - ElapsedFlyingMs) instead of inferring elapsed time from the first
    /// MultiplierUpdated value — that avoids both clock-skew and growth-formula mismatches,
    /// keeping every connected/reconnected client showing the exact same live multiplier.
    /// </summary>
    double? ElapsedFlyingMs
)
{
    public static RoundStateDto From(Round round, IEnumerable<Bet> bets) => new(
        round.RoundId, round.Phase, round.ServerSeedHash, round.ClientSeed, round.Nonce,
        bets.Select(BetDto.From),
        round.Phase == GamePhase.Flying && round.FlyingStartedAt.HasValue
            ? (DateTime.UtcNow - round.FlyingStartedAt.Value).TotalMilliseconds
            : null
    );
}

/// <summary>Broadcast at the start of every new round.</summary>
public record RoundStartedDto(
    string RoundId,
    GamePhase Phase,
    string ServerSeedHash,
    string ClientSeed,
    long Nonce,
    int WaitingSeconds
)
{
    public static RoundStartedDto From(Round round, int waitingSeconds) => new(
        round.RoundId, round.Phase, round.ServerSeedHash, round.ClientSeed, round.Nonce,
        waitingSeconds
    );
}

/// <summary>Broadcast when the round ends. Includes server seed reveal for provable fairness.</summary>
public record RoundCrashedDto(
    string RoundId,
    decimal CrashMultiplier,
    DateTime? CrashedAt,
    string ServerSeed,
    string ServerSeedHash,
    string ClientSeed,
    long Nonce,
    IEnumerable<BetDto> Bets,
    IEnumerable<HistoryEntryDto> History
)
{
    public static RoundCrashedDto From(Round round, IEnumerable<HistoryEntryDto> history) => new(
        round.RoundId, round.CrashMultiplier, round.CrashedAt,
        round.ServerSeed, round.ServerSeedHash, round.ClientSeed, round.Nonce,
        round.Bets.Select(BetDto.From),
        history
    );
}

/// <summary>Compact history entry — used in RoundCrashed payload and the History event.</summary>
public record HistoryEntryDto(string RoundId, decimal CrashMultiplier, DateTime? CrashedAt)
{
    public static HistoryEntryDto From(Round round) => new(round.RoundId, round.CrashMultiplier, round.CrashedAt);
}
