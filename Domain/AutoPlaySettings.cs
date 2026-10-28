namespace Worldiator.Server.Domain;

public class AutoPlaySettings
{
    public bool Enabled { get; set; }
    public int? MaxRounds { get; set; }
    public decimal BetAmount { get; set; }
    public decimal? AutoCashOutAt { get; set; }
    public decimal? StopIfCashExceeds { get; set; }
    public decimal? StopIfCashDecreases { get; set; }
    public decimal? StopIfSingleWinExceeds { get; set; }
}
