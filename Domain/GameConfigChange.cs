namespace Worldiator.Server.Domain;

/// <summary>One back-office edit to a game's configuration (dbo.GameConfigAudit). Values are JSON.</summary>
public record GameConfigChange(long AuditId, string GameId, string Section, string? OldValue, string NewValue, string ChangedBy, DateTime ChangedAt)
{
    /// <summary>The game's catalogue entry (limits, RNG, timings…). ServerKey is never written to the audit.</summary>
    public const string GameSection = "Game";
    public const string EvolutionSection = "Evolution";
}
