using Worldiator.Server.Domain;

namespace Worldiator.Server.Contracts;

/// <summary>Game Back Office accounts. Usernames are case-insensitive.</summary>
public interface IBackOfficeUserStore
{
    Task<IReadOnlyList<BackOfficeUser>> ListAsync();
    Task<BackOfficeUser?> FindAsync(int userId);
    Task<BackOfficeUser?> FindByUsernameAsync(string username);

    /// <summary>Returns null when the username is taken.</summary>
    Task<BackOfficeUser?> CreateAsync(string username, string displayName, string passwordHash, BackOfficeRole role);

    /// <summary>Saves DisplayName, PasswordHash, Role, Enabled and SecurityStamp.</summary>
    Task UpdateAsync(BackOfficeUser user);

    Task RecordLoginAsync(int userId);
}
