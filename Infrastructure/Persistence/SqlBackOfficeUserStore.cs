using Dapper;
using Microsoft.Data.SqlClient;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;

namespace Worldiator.Server.Infrastructure.Persistence;

/// <summary>SQL Server back-office accounts (table: BackOfficeUsers.sql).</summary>
public class SqlBackOfficeUserStore(IConfiguration config) : IBackOfficeUserStore
{
    private readonly string _connStr = config.GetConnectionString("DefaultConnection")!;

    private const string Columns = "UserId, Username, DisplayName, PasswordHash, Role, Enabled, SecurityStamp, CreatedAt, LastLoginAt";
    private const int UniqueViolation = 2627;

    public async Task<IReadOnlyList<BackOfficeUser>> ListAsync()
    {
        using var conn = new SqlConnection(_connStr);
        return (await conn.QueryAsync<Row>($"SELECT {Columns} FROM dbo.BackOfficeUsers ORDER BY Username")).Select(r => r.ToDomain()).ToList();
    }

    public async Task<BackOfficeUser?> FindAsync(int userId)
    {
        using var conn = new SqlConnection(_connStr);
        return (await conn.QueryAsync<Row>($"SELECT {Columns} FROM dbo.BackOfficeUsers WHERE UserId = @userId", new { userId }))
            .FirstOrDefault()?.ToDomain();
    }

    public async Task<BackOfficeUser?> FindByUsernameAsync(string username)
    {
        using var conn = new SqlConnection(_connStr);
        return (await conn.QueryAsync<Row>($"SELECT {Columns} FROM dbo.BackOfficeUsers WHERE Username = @username", new { username }))
            .FirstOrDefault()?.ToDomain();
    }

    public async Task<BackOfficeUser?> CreateAsync(string username, string displayName, string passwordHash, BackOfficeRole role)
    {
        using var conn = new SqlConnection(_connStr);
        try
        {
            var row = await conn.QuerySingleAsync<Row>(
                $"INSERT INTO dbo.BackOfficeUsers (Username, DisplayName, PasswordHash, Role, SecurityStamp) OUTPUT INSERTED.{Columns.Replace(", ", ", INSERTED.")} " +
                "VALUES (@username, @displayName, @passwordHash, @role, @stamp)",
                new { username, displayName, passwordHash, role = role.ToString(), stamp = BackOfficeStamp.New() });
            return row.ToDomain();
        }
        catch (SqlException ex) when (ex.Number == UniqueViolation)
        {
            return null;
        }
    }

    public async Task UpdateAsync(BackOfficeUser user)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.ExecuteAsync(
            "UPDATE dbo.BackOfficeUsers SET DisplayName = @DisplayName, PasswordHash = @PasswordHash, Role = @Role, " +
            "Enabled = @Enabled, SecurityStamp = @SecurityStamp WHERE UserId = @UserId",
            new { user.UserId, user.DisplayName, user.PasswordHash, Role = user.Role.ToString(), user.Enabled, user.SecurityStamp });
    }

    public async Task RecordLoginAsync(int userId)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.ExecuteAsync("UPDATE dbo.BackOfficeUsers SET LastLoginAt = SYSUTCDATETIME() WHERE UserId = @userId", new { userId });
    }

    private sealed class Row
    {
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string SecurityStamp { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastLoginAt { get; set; }

        public BackOfficeUser ToDomain() => new(UserId, Username, DisplayName, PasswordHash,
            Enum.Parse<BackOfficeRole>(Role, ignoreCase: true), Enabled, SecurityStamp, CreatedAt, LastLoginAt);
    }
}
