using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure;

/// <summary>Back-office accounts in memory — used when no SQL connection is configured. Lost on restart.</summary>
public class InMemoryBackOfficeUserStore : IBackOfficeUserStore
{
    private readonly List<BackOfficeUser> _users = [];
    private int _nextId = 1;

    public Task<IReadOnlyList<BackOfficeUser>> ListAsync()
    {
        lock (_users) return Task.FromResult<IReadOnlyList<BackOfficeUser>>(_users.OrderBy(u => u.Username).ToList());
    }

    public Task<BackOfficeUser?> FindAsync(int userId)
    {
        lock (_users) return Task.FromResult(_users.FirstOrDefault(u => u.UserId == userId));
    }

    public Task<BackOfficeUser?> FindByUsernameAsync(string username)
    {
        lock (_users) return Task.FromResult(_users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<BackOfficeUser?> CreateAsync(string username, string displayName, string passwordHash, BackOfficeRole role)
    {
        lock (_users)
        {
            if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult<BackOfficeUser?>(null);
            var user = new BackOfficeUser(_nextId++, username, displayName, passwordHash, role, true, BackOfficeStamp.New(), DateTime.UtcNow, null);
            _users.Add(user);
            return Task.FromResult<BackOfficeUser?>(user);
        }
    }

    public Task UpdateAsync(BackOfficeUser user)
    {
        lock (_users)
        {
            var i = _users.FindIndex(u => u.UserId == user.UserId);
            if (i >= 0) _users[i] = user;
        }
        return Task.CompletedTask;
    }

    public Task RecordLoginAsync(int userId)
    {
        lock (_users)
        {
            var i = _users.FindIndex(u => u.UserId == userId);
            if (i >= 0) _users[i] = _users[i] with { LastLoginAt = DateTime.UtcNow };
        }
        return Task.CompletedTask;
    }
}
