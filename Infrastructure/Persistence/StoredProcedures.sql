USE [FeedingFranzee];
GO

CREATE OR ALTER PROCEDURE dbo.usp_UpsertRound
    @RoundId nvarchar(32),
    @Nonce bigint,
    @ServerSeed nvarchar(64),
    @ServerSeedHash nvarchar(64),
    @ClientSeed nvarchar(64),
    @CrashMultiplier decimal(10,2),
    @Phase nvarchar(10),
    @StartedAt datetime2,
    @FlyingStartedAt datetime2 = NULL,
    @CrashedAt datetime2 = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM dbo.Rounds WHERE RoundId = @RoundId)
    BEGIN
        UPDATE dbo.Rounds
        SET Nonce = @Nonce,
            ServerSeed = @ServerSeed,
            ServerSeedHash = @ServerSeedHash,
            ClientSeed = @ClientSeed,
            CrashMultiplier = @CrashMultiplier,
            Phase = @Phase,
            StartedAt = @StartedAt,
            FlyingStartedAt = @FlyingStartedAt,
            CrashedAt = @CrashedAt
        WHERE RoundId = @RoundId;
    END
    ELSE
    BEGIN
        INSERT INTO dbo.Rounds
            (RoundId, Nonce, ServerSeed, ServerSeedHash, ClientSeed, CrashMultiplier, Phase, StartedAt, FlyingStartedAt, CrashedAt, CreatedAt)
        VALUES
            (@RoundId, @Nonce, @ServerSeed, @ServerSeedHash, @ClientSeed, @CrashMultiplier, @Phase, @StartedAt, @FlyingStartedAt, @CrashedAt, SYSUTCDATETIME());
    END
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_UpsertBet
    @BetId nvarchar(32),
    @RoundId nvarchar(32),
    @PlayerId nvarchar(100),
    @Username nvarchar(100),
    @SlotIndex int,
    @Amount decimal(12,2),
    @Currency nvarchar(10),
    @CashedOutAt decimal(10,2) = NULL,
    @WinAmount decimal(12,2) = NULL,
    @AutoCashOutAt decimal(10,2) = NULL,
    @DebitTransactionId nvarchar(100) = NULL,
    @CreditTransactionId nvarchar(100) = NULL,
    @PlacedAt datetime2
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM dbo.Bets WHERE BetId = @BetId)
    BEGIN
        UPDATE dbo.Bets
        SET RoundId = @RoundId,
            PlayerId = @PlayerId,
            Username = @Username,
            SlotIndex = @SlotIndex,
            Amount = @Amount,
            Currency = @Currency,
            CashedOutAt = @CashedOutAt,
            WinAmount = @WinAmount,
            AutoCashOutAt = @AutoCashOutAt,
            DebitTransactionId = @DebitTransactionId,
            CreditTransactionId = @CreditTransactionId,
            PlacedAt = @PlacedAt
        WHERE BetId = @BetId;
    END
    ELSE
    BEGIN
        INSERT INTO dbo.Bets
            (BetId, RoundId, PlayerId, Username, SlotIndex, Amount, Currency, CashedOutAt, WinAmount, AutoCashOutAt, DebitTransactionId, CreditTransactionId, PlacedAt, CreatedAt)
        VALUES
            (@BetId, @RoundId, @PlayerId, @Username, @SlotIndex, @Amount, @Currency, @CashedOutAt, @WinAmount, @AutoCashOutAt, @DebitTransactionId, @CreditTransactionId, @PlacedAt, SYSUTCDATETIME());
    END
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_GetRecentRounds
    @Count int = 50
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Count) RoundId, Nonce, ServerSeed, ServerSeedHash, ClientSeed, CrashMultiplier, Phase, StartedAt, FlyingStartedAt, CrashedAt, CreatedAt
    INTO #RecentRounds
    FROM dbo.Rounds
    ORDER BY CrashedAt DESC;

    SELECT * FROM #RecentRounds ORDER BY CrashedAt DESC;

    SELECT b.BetId, b.RoundId, b.PlayerId, b.Username, b.SlotIndex, b.Amount, b.Currency, b.CashedOutAt, b.WinAmount, b.AutoCashOutAt, b.DebitTransactionId, b.CreditTransactionId, b.PlacedAt, b.CreatedAt
    FROM dbo.Bets b
    INNER JOIN #RecentRounds r ON r.RoundId = b.RoundId;
END
GO

-- Per-player bet history — powers the "My Bets" view (GameHub.GetMyBets / GET api/game/my-bets).
CREATE OR ALTER PROCEDURE dbo.usp_GetPlayerBetHistory
    @PlayerId nvarchar(100),
    @Count int = 50
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Count) BetId, RoundId, PlayerId, Username, SlotIndex, Amount, Currency, CashedOutAt, WinAmount, AutoCashOutAt, DebitTransactionId, CreditTransactionId, PlacedAt, CreatedAt
    FROM dbo.Bets
    WHERE PlayerId = @PlayerId
    ORDER BY PlacedAt DESC;
END
GO

-- Past round crash-multiplier history only — lighter than usp_GetRecentRounds
-- (no bet join) for a multiplier ticker / results strip.
CREATE OR ALTER PROCEDURE dbo.usp_GetRoundMultiplierHistory
    @Count int = 50
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Count) RoundId, Nonce, CrashMultiplier, CrashedAt
    FROM dbo.Rounds
    WHERE CrashedAt IS NOT NULL
    ORDER BY CrashedAt DESC;
END
GO
