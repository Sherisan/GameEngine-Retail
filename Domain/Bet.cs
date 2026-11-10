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

    /// <summary>
    /// Set when the bet is a promo free bet: no debit was made for it, and a win pays only what the promotion
    /// allows (PromoEngine.SettleFreeBetAsync) instead of a normal credit.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Promotions.PromoAward? FreeBet { get; set; }

    // ── Interactive (Feeding Frenzy) mode ────────────────────────────────
    /// <summary>Payout boost earned by eating fish, as a fraction (0.005 = +0.5%); the payout is curve × (1 + boost).</summary>
    public decimal BoostMultiplier { get; set; }
    /// <summary>Spawn ids already eaten by this bet (each fish is edible once).</summary>
    public HashSet<int> EatenFishIds { get; } = new();
}
