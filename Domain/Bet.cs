namespace Worldiator.Server.Domain;

public class Bet
{
    public string BetId { get; set; } = Guid.NewGuid().ToString("N");
    public string RoundId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public int SlotIndex { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ZAR";
    public decimal? CashedOutAt { get; set; }
    public decimal? WinAmount { get; set; }
    public decimal? AutoCashOutAt { get; set; }
    public string? DebitTransactionId { get; set; }
    public string? CreditTransactionId { get; set; }
    public DateTime PlacedAt { get; set; } = DateTime.UtcNow;
    public bool HasGambled { get; set; }
    /// <summary>
    /// Which game the bet was placed from (the session token's "game" claim, e.g. "comet-rider").
    /// Several games share this engine's rounds; this decides which title the wallet books the bet under.
    /// Null / "feeding-frenzy" = Feeding Frenzy.
    /// </summary>
    public string? GameId { get; set; }

    // ── Interactive (Feeding Frenzy) mode ────────────────────────────────
    /// <summary>Extra multiplier earned by eating fish; added to the curve multiplier at cash-out.</summary>
    public decimal BoostMultiplier { get; set; }
    /// <summary>True once the player's fish hit a mine — the bet can no longer cash out.</summary>
    public bool IsDead { get; set; }
    /// <summary>Spawn ids already eaten by this bet (each fish is edible once).</summary>
    public HashSet<int> EatenFishIds { get; } = new();
}
