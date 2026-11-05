using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Worldiator.Server.Contracts;
using Worldiator.Server.Domain;
using Worldiator.Server.Domain.MarginCall;

namespace Worldiator.Server.Infrastructure.Persistence;

/// <summary>
/// SQL Server game config (tables: GameConfiguration.sql, GameCatalogue.sql). Reads are served from immutable
/// snapshots that RefreshAsync swaps in. Until the first successful read — or for a table that doesn't exist —
/// appsettings "Games" and the built-in evolution ladders are served, so the games keep running.
/// </summary>
public class SqlGameConfigStore : IGameConfigStore
{
    private const int InvalidObjectName = 208;
    private const int InvalidColumnName = 207; // dbo.Games from before a column was added — re-run GameCatalogue.sql
    private const string GameColumns =
        "GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, ServerKey, MinBet, MaxBet, MaxWin, Enabled, RngJson, LiveJson, RouletteJson, TradingJson, BlackjackJson, SortOrder";

    private readonly string _connStr;
    private readonly List<GameDefinition> _seed;
    private readonly ILogger<SqlGameConfigStore> _logger;
    private readonly HashSet<string> _missingTablesLogged = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private volatile IReadOnlyList<GameDefinition> _games;
    private volatile IReadOnlyDictionary<string, IReadOnlyList<EvolutionTier>> _tiers = EvolutionTiers.Defaults;

    public SqlGameConfigStore(IConfiguration config, ILogger<SqlGameConfigStore> logger)
    {
        _connStr = config.GetConnectionString("DefaultConnection")!;
        _logger = logger;
        _seed = GameCatalogue.FromConfiguration(config);
        GameCatalogue.Validate(_seed);
        _games = _seed;
    }

    public IReadOnlyList<GameDefinition> Games => _games;

    public IReadOnlyList<EvolutionTier> GetEvolutionTiers(string gameId) =>
        _tiers.TryGetValue(gameId, out var tiers) ? tiers : [];

    // ── Load ────────────────────────────────────────────────────────────────

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await ImportSeedAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is InvalidObjectName or InvalidColumnName)
        {
            LogMissingTable("dbo.Games", "GameCatalogue.sql");
        }
        await RefreshAsync(ct);
    }

    /// <summary>
    /// Copies the appsettings games that dbo.Games doesn't have yet. Existing rows are left alone, except that a
    /// Margin Call row with no Trading block yet gets the appsettings one (filled in once, never overwritten).
    /// </summary>
    private async Task ImportSeedAsync(CancellationToken ct)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        await LockAsync(conn, tx, "gameconfig:catalogue");

        var existing = (await conn.QueryAsync<string>("SELECT GameId FROM dbo.Games", transaction: tx))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = await conn.QuerySingleAsync<int>("SELECT ISNULL(MAX(SortOrder), 0) FROM dbo.Games", transaction: tx);

        var added = new List<string>();
        foreach (var game in _seed.Where(g => !existing.Contains(g.GameId)))
        {
            await UpsertAsync(conn, tx, game, ++order, "appsettings");
            await AuditAsync(conn, tx, game.GameId, GameConfigChange.GameSection, null, GameCatalogue.AuditJson(game), "appsettings");
            added.Add(game.GameId);
        }

        var filled = new List<string>();
        foreach (var seed in _seed.Where(g => g.Trading != null && existing.Contains(g.GameId)))
        {
            var row = await conn.QuerySingleAsync<GameRow>(
                $"SELECT {GameColumns} FROM dbo.Games WHERE GameId = @GameId", new { seed.GameId }, tx);
            if (row.TradingJson != null || !string.Equals(row.Engine, nameof(EngineKind.MarginCall), StringComparison.OrdinalIgnoreCase))
                continue;
            var old = row.ToDomain();
            var game = GameCatalogue.Clone(old);
            game.Trading = seed.Trading;
            await UpsertAsync(conn, tx, game, row.SortOrder, "appsettings");
            await AuditAsync(conn, tx, game.GameId, GameConfigChange.GameSection, GameCatalogue.AuditJson(old), GameCatalogue.AuditJson(game), "appsettings");
            filled.Add(game.GameId);
        }

        tx.Commit();
        if (added.Count > 0)
            _logger.LogInformation("Imported games from appsettings into dbo.Games: {Games}", string.Join(", ", added));
        if (filled.Count > 0)
            _logger.LogInformation("Copied the appsettings MarginCall trading settings into dbo.Games: {Games}", string.Join(", ", filled));
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var conn = new SqlConnection(_connStr);
        await conn.OpenAsync(ct);

        try
        {
            var rows = await conn.QueryAsync<GameRow>(new CommandDefinition(
                $"SELECT {GameColumns} FROM dbo.Games ORDER BY SortOrder, GameId", cancellationToken: ct));
            var games = new List<GameDefinition>();
            foreach (var row in rows)
            {
                // A row edited by hand into something invalid keeps serving its last good version.
                try
                {
                    var game = row.ToDomain();
                    game.Validate();
                    games.Add(game);
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
                {
                    var previous = _games.FirstOrDefault(g => string.Equals(g.GameId, row.GameId, StringComparison.OrdinalIgnoreCase));
                    _logger.LogError("dbo.Games row {GameId} is invalid ({Error}) — {Action}", row.GameId, ex.Message,
                        previous == null ? "skipping it" : "keeping the previous version");
                    if (previous != null) games.Add(previous);
                }
            }

            if (games.Count == 0)
                _logger.LogWarning("dbo.Games is empty — serving the games from appsettings");
            else if (GameCatalogue.Problem(games) is { } problem)
                _logger.LogError("dbo.Games is invalid ({Problem}) — keeping the previous catalogue", problem);
            else
                _games = games;
        }
        catch (SqlException ex) when (ex.Number is InvalidObjectName or InvalidColumnName)
        {
            LogMissingTable("dbo.Games", "GameCatalogue.sql");
        }

        try
        {
            var rows = await conn.QueryAsync<TierRow>(new CommandDefinition(
                "SELECT GameId, Level, Code, Name, Multiplier FROM dbo.GameEvolutionTiers", cancellationToken: ct));

            // Games with rows in the database use them; the defaults only fill in games the database doesn't have.
            var tiers = new Dictionary<string, IReadOnlyList<EvolutionTier>>(EvolutionTiers.Defaults, StringComparer.OrdinalIgnoreCase);
            foreach (var game in rows.GroupBy(r => r.GameId, StringComparer.OrdinalIgnoreCase))
            {
                var ladder = game.OrderBy(r => r.Level).Select(r => r.ToDomain()).ToList();
                var errors = EvolutionTiers.Validate(ladder);
                if (errors.Count > 0)
                {
                    _logger.LogError("dbo.GameEvolutionTiers for {GameId} is invalid ({Errors}) — keeping the previous ladder",
                        game.Key, string.Join(" ", errors));
                    tiers[game.Key] = GetEvolutionTiers(game.Key);
                    continue;
                }
                tiers[game.Key] = ladder;
            }
            _tiers = tiers;
        }
        catch (SqlException ex) when (ex.Number is InvalidObjectName or InvalidColumnName)
        {
            LogMissingTable("dbo.GameEvolutionTiers", "GameConfiguration.sql");
        }
    }

    private void LogMissingTable(string table, string script)
    {
        lock (_missingTablesLogged)
            if (!_missingTablesLogged.Add(table)) return;
        _logger.LogWarning("{Table} doesn't exist or is missing a column — serving built-in / appsettings values. Run Infrastructure/Persistence/{Script} to manage it from the back office.",
            table, script);
    }

    // ── Save ────────────────────────────────────────────────────────────────

    public async Task SaveGameAsync(GameDefinition game, string changedBy)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            await LockAsync(conn, tx, "gameconfig:catalogue");

            var old = (await conn.QueryAsync<GameRow>(
                $"SELECT {GameColumns} FROM dbo.Games WHERE GameId = @GameId", new { game.GameId }, tx)).FirstOrDefault();
            var order = old?.SortOrder
                ?? await conn.QuerySingleAsync<int>("SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM dbo.Games", transaction: tx);

            await UpsertAsync(conn, tx, game, order, changedBy);
            await AuditAsync(conn, tx, game.GameId, GameConfigChange.GameSection,
                old == null ? null : GameCatalogue.AuditJson(old.ToDomain()), GameCatalogue.AuditJson(game), changedBy);
            tx.Commit();

            // This instance serves the change straight away; the others pick it up on their next refresh.
            var games = _games.ToList();
            var index = games.FindIndex(g => string.Equals(g.GameId, game.GameId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) games[index] = game;
            else games.Add(game);
            _games = games;
        }
        finally { _writeLock.Release(); }
    }

    public async Task SaveEvolutionTiersAsync(string gameId, IReadOnlyList<EvolutionTier> tiers, string changedBy)
    {
        var ordered = tiers.OrderBy(t => t.Level).ToList();

        await _writeLock.WaitAsync();
        try
        {
            using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            // One editor per game at a time, across every API instance.
            await LockAsync(conn, tx, $"gameconfig:evolution:{gameId}");

            var old = (await conn.QueryAsync<TierRow>(
                "SELECT GameId, Level, Code, Name, Multiplier FROM dbo.GameEvolutionTiers WHERE GameId = @gameId ORDER BY Level",
                new { gameId }, tx)).Select(r => r.ToDomain()).ToList();

            await conn.ExecuteAsync("DELETE FROM dbo.GameEvolutionTiers WHERE GameId = @gameId", new { gameId }, tx);
            await conn.ExecuteAsync(
                "INSERT INTO dbo.GameEvolutionTiers (GameId, Level, Code, Name, Multiplier, UpdatedAt, UpdatedBy) " +
                "VALUES (@GameId, @Level, @Code, @Name, @Multiplier, SYSUTCDATETIME(), @ChangedBy)",
                ordered.Select(t => new { GameId = gameId, t.Level, t.Code, t.Name, t.Multiplier, ChangedBy = changedBy }), tx);
            await AuditAsync(conn, tx, gameId, GameConfigChange.EvolutionSection,
                old.Count == 0 ? null : JsonSerializer.Serialize(old), JsonSerializer.Serialize(ordered), changedBy);

            tx.Commit();
            _tiers = new Dictionary<string, IReadOnlyList<EvolutionTier>>(_tiers, StringComparer.OrdinalIgnoreCase) { [gameId] = ordered };
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<GameConfigChange>> GetHistoryAsync(string gameId, string? section, int count)
    {
        using var conn = new SqlConnection(_connStr);
        var rows = await conn.QueryAsync<GameConfigChange>(
            "SELECT TOP (@count) AuditId, GameId, Section, OldValue, NewValue, ChangedBy, ChangedAt FROM dbo.GameConfigAudit " +
            "WHERE GameId = @gameId AND (@section IS NULL OR Section = @section) ORDER BY ChangedAt DESC, AuditId DESC",
            new { gameId, section, count });
        return rows.ToList();
    }

    // ── SQL helpers ─────────────────────────────────────────────────────────

    private static async Task LockAsync(SqlConnection conn, SqlTransaction tx, string resource)
    {
        var result = await conn.QuerySingleAsync<int>(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource, 'Exclusive', 'Transaction', 10000; SELECT @r;",
            new { Resource = resource }, tx);
        if (result < 0)
            throw new TimeoutException("Timed out waiting for the game config lock.");
    }

    private static Task UpsertAsync(SqlConnection conn, SqlTransaction tx, GameDefinition game, int sortOrder, string changedBy) =>
        conn.ExecuteAsync(
            """
            MERGE dbo.Games WITH (HOLDLOCK) AS t
            USING (SELECT @GameId AS GameId) AS s ON t.GameId = s.GameId
            WHEN MATCHED THEN UPDATE SET
                ProviderGameId = @ProviderGameId, Name = @Name, Type = @Type, Engine = @Engine, LaunchUrl = @LaunchUrl,
                ServerKey = @ServerKey, MinBet = @MinBet, MaxBet = @MaxBet, MaxWin = @MaxWin, Enabled = @Enabled,
                RngJson = @RngJson, LiveJson = @LiveJson, RouletteJson = @RouletteJson, TradingJson = @TradingJson, BlackjackJson = @BlackjackJson, SortOrder = @SortOrder,
                UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @ChangedBy
            WHEN NOT MATCHED THEN INSERT
                (GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, ServerKey, MinBet, MaxBet, MaxWin, Enabled,
                 RngJson, LiveJson, RouletteJson, TradingJson, BlackjackJson, SortOrder, UpdatedBy)
                VALUES (@GameId, @ProviderGameId, @Name, @Type, @Engine, @LaunchUrl, @ServerKey, @MinBet, @MaxBet, @MaxWin, @Enabled,
                        @RngJson, @LiveJson, @RouletteJson, @TradingJson, @BlackjackJson, @SortOrder, @ChangedBy);
            """,
            new
            {
                game.GameId,
                game.ProviderGameId,
                game.Name,
                Type = game.Type.ToString(),
                Engine = game.Engine.ToString(),
                game.LaunchUrl,
                game.ServerKey,
                game.MinBet,
                game.MaxBet,
                game.MaxWin,
                game.Enabled,
                RngJson = game.Rng == null ? null : JsonSerializer.Serialize(game.Rng, GameCatalogue.Json),
                LiveJson = game.Live == null ? null : JsonSerializer.Serialize(game.Live, GameCatalogue.Json),
                RouletteJson = game.Roulette == null ? null : JsonSerializer.Serialize(game.Roulette, GameCatalogue.Json),
                TradingJson = game.Trading == null ? null : JsonSerializer.Serialize(game.Trading, GameCatalogue.Json),
                BlackjackJson = game.Blackjack == null ? null : JsonSerializer.Serialize(game.Blackjack, GameCatalogue.Json),
                SortOrder = sortOrder,
                ChangedBy = changedBy,
            }, tx);

    private static Task AuditAsync(SqlConnection conn, SqlTransaction tx, string gameId, string section, string? oldValue, string newValue, string changedBy) =>
        conn.ExecuteAsync(
            "INSERT INTO dbo.GameConfigAudit (GameId, Section, OldValue, NewValue, ChangedBy) VALUES (@gameId, @section, @oldValue, @newValue, @changedBy)",
            new { gameId, section, oldValue, newValue, changedBy }, tx);

    private sealed class GameRow
    {
        public string GameId { get; set; } = string.Empty;
        public long? ProviderGameId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Engine { get; set; } = string.Empty;
        public string LaunchUrl { get; set; } = string.Empty;
        public string ServerKey { get; set; } = string.Empty;
        public decimal MinBet { get; set; }
        public decimal MaxBet { get; set; }
        public decimal MaxWin { get; set; }
        public bool Enabled { get; set; }
        public string? RngJson { get; set; }
        public string? LiveJson { get; set; }
        public string? RouletteJson { get; set; }
        public string? TradingJson { get; set; }
        public string? BlackjackJson { get; set; }
        public int SortOrder { get; set; }

        public GameDefinition ToDomain() => new()
        {
            GameId = GameId,
            ProviderGameId = ProviderGameId,
            Name = Name,
            Type = Enum.Parse<GameType>(Type, ignoreCase: true),
            Engine = Enum.Parse<EngineKind>(Engine, ignoreCase: true),
            LaunchUrl = LaunchUrl,
            ServerKey = ServerKey,
            MinBet = MinBet,
            MaxBet = MaxBet,
            MaxWin = MaxWin,
            Enabled = Enabled,
            Rng = RngJson == null ? null : JsonSerializer.Deserialize<RngSettings>(RngJson, GameCatalogue.Json),
            Live = LiveJson == null ? null : JsonSerializer.Deserialize<LiveCrashSettings>(LiveJson, GameCatalogue.Json),
            Roulette = RouletteJson == null ? null : JsonSerializer.Deserialize<RouletteSettings>(RouletteJson, GameCatalogue.Json),
            Trading = TradingJson == null ? null : JsonSerializer.Deserialize<TradingSettings>(TradingJson, GameCatalogue.Json),
            Blackjack = BlackjackJson == null ? null : JsonSerializer.Deserialize<Domain.Blackjack.BlackjackSettings>(BlackjackJson, GameCatalogue.Json),
        };
    }

    private sealed class TierRow
    {
        public string GameId { get; set; } = string.Empty;
        public int Level { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal Multiplier { get; set; }

        public EvolutionTier ToDomain() => new(Level, Code, Name, Multiplier);
    }
}
