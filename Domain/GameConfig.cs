namespace Worldiator.Server.Domain;

public class GameConfig
{
    public GameMode Mode { get; set; } = GameMode.Standard;
    public int WaitingSeconds { get; set; } = 5;
    public decimal MinBet { get; set; } = 1.00m;
    public decimal MaxBet { get; set; } = 10000.00m;
    public decimal MaxWin { get; set; } = 500000.00m;
    public double MultiplierGrowthRate { get; set; } = 0.00006;
}
