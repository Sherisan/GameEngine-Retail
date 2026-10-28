namespace Worldiator.Server.Domain;

public class Round
{
    public string RoundId { get; set; } = Guid.NewGuid().ToString("N");
    public long Nonce { get; set; }
    public string ServerSeed { get; set; } = string.Empty;
    public string ServerSeedHash { get; set; } = string.Empty;
    public string ClientSeed { get; set; } = string.Empty;
    public decimal CrashMultiplier { get; set; }
    public GamePhase Phase { get; set; } = GamePhase.Waiting;
    public DateTime StartedAt { get; set; }
    /// <summary>
    /// Set when the round transitions from Waiting → Flying.
    /// This is the authoritative reference for multiplier calculations — StartedAt must NOT be used.
    /// </summary>
    public DateTime? FlyingStartedAt { get; set; }
    public DateTime? CrashedAt { get; set; }
    public List<Bet> Bets { get; set; } = new();
}
