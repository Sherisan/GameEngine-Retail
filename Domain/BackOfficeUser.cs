using System.Text.Json.Serialization;

namespace Worldiator.Server.Domain;

/// <summary>
/// What a back-office account may do. Each role includes the ones before it:
///   Viewer — read every game's configuration and history.
///   Editor — also change games and evolution ladders.
///   Admin  — also manage back-office accounts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackOfficeRole { Viewer, Editor, Admin }

public static class BackOfficeStamp
{
    /// <summary>A new security stamp — issued again whenever an account's password, role or enabled flag changes.</summary>
    public static string New() => Guid.NewGuid().ToString("N");
}

/// <summary>A Game Back Office sign-in account (dbo.BackOfficeUsers).</summary>
public record BackOfficeUser(
    int UserId,
    string Username,
    string DisplayName,
    string PasswordHash,
    BackOfficeRole Role,
    bool Enabled,
    string SecurityStamp,
    DateTime CreatedAt,
    DateTime? LastLoginAt);
