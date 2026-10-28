namespace Worldiator.Server.Domain;

/// <summary>Thrown when the RGS rejects a wallet operation.</summary>
public class RGSException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}
