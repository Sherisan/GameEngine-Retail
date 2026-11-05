-- Per-game configuration managed from the Game Back Office (api/backoffice/*).
-- Safe to re-run: tables are created once and the seed only inserts rows that are missing,
-- so values already edited in the back office are never overwritten.

USE [FeedingFranzee];
GO

-- Evolution ladder for the /gamehub crash games: the round multiplier at which the player's
-- fish (Feeding Frenzy) or ship (Comet Rider) evolves into each tier.
-- Level 0 is the ambient "food" tier below the 1.00x launch point, Level 1 is the starting tier
-- (always 1.00), and each higher level must need a larger multiplier than the one before.
-- Code is the art key the frontend draws (e.g. "shark") — the back office edits multipliers and
-- names, not codes.
IF OBJECT_ID(N'dbo.GameEvolutionTiers') IS NULL
BEGIN
    CREATE TABLE dbo.GameEvolutionTiers (
        [GameId]     nvarchar(64)  NOT NULL,
        [Level]      int           NOT NULL,
        [Code]       nvarchar(32)  NOT NULL,
        [Name]       nvarchar(64)  NOT NULL,
        [Multiplier] decimal(10,2) NOT NULL,
        [UpdatedAt]  datetime2     NOT NULL CONSTRAINT [DF_GameEvolutionTiers_UpdatedAt] DEFAULT SYSUTCDATETIME(),
        [UpdatedBy]  nvarchar(100) NOT NULL CONSTRAINT [DF_GameEvolutionTiers_UpdatedBy] DEFAULT N'seed',
        CONSTRAINT [PK_GameEvolutionTiers] PRIMARY KEY ([GameId], [Level]),
        CONSTRAINT [UQ_GameEvolutionTiers_Code] UNIQUE ([GameId], [Code]),
        CONSTRAINT [CK_GameEvolutionTiers_Multiplier] CHECK ([Multiplier] > 0)
    );
END;
GO

-- Every back-office change, with the full section before and after (JSON).
IF OBJECT_ID(N'dbo.GameConfigAudit') IS NULL
BEGIN
    CREATE TABLE dbo.GameConfigAudit (
        [AuditId]   bigint IDENTITY(1,1) NOT NULL,
        [GameId]    nvarchar(64)  NOT NULL,
        [Section]   nvarchar(32)  NOT NULL,
        [OldValue]  nvarchar(max) NULL,
        [NewValue]  nvarchar(max) NOT NULL,
        [ChangedBy] nvarchar(100) NOT NULL,
        [ChangedAt] datetime2     NOT NULL CONSTRAINT [DF_GameConfigAudit_ChangedAt] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_GameConfigAudit] PRIMARY KEY ([AuditId])
    );
    CREATE INDEX [IX_GameConfigAudit_Game_Section] ON dbo.GameConfigAudit ([GameId], [Section], [ChangedAt] DESC);
END;
GO

-- Seed with the values the games shipped with.
INSERT INTO dbo.GameEvolutionTiers ([GameId], [Level], [Code], [Name], [Multiplier])
SELECT s.GameId, s.Level, s.Code, s.Name, s.Multiplier
FROM (VALUES
    (N'feeding-frenzy', 0, N'sardine',    N'Sardine',     0.50),
    (N'feeding-frenzy', 1, N'clownfish',  N'Clownfish',   1.00),
    (N'feeding-frenzy', 2, N'grouper',    N'Grouper',     2.00),
    (N'feeding-frenzy', 3, N'tuna',       N'Tuna',        5.00),
    (N'feeding-frenzy', 4, N'shark',      N'Shark',      10.00),
    (N'feeding-frenzy', 5, N'whaleshark', N'Whale Shark', 25.00),
    (N'comet-rider',    0, N'probe',      N'Probe',       0.50),
    (N'comet-rider',    1, N'scout',      N'Scout',       1.00),
    (N'comet-rider',    2, N'fighter',    N'Fighter',     2.00),
    (N'comet-rider',    3, N'cruiser',    N'Cruiser',     5.00),
    (N'comet-rider',    4, N'destroyer',  N'Destroyer',  10.00),
    (N'comet-rider',    5, N'mothership', N'Mothership', 25.00),
    (N'arctic-appetite', 0, N'krill',     N'Krill',          0.50),
    (N'arctic-appetite', 1, N'penguin',   N'Penguin',        1.00),
    (N'arctic-appetite', 2, N'seal',      N'Seal',           2.00),
    (N'arctic-appetite', 3, N'polarbear', N'Polar Bear',     5.00),
    (N'arctic-appetite', 4, N'orca',      N'Orca',          10.00),
    (N'arctic-appetite', 5, N'leviathan', N'Ice Leviathan', 25.00)
) AS s (GameId, Level, Code, Name, Multiplier)
WHERE NOT EXISTS (SELECT 1 FROM dbo.GameEvolutionTiers t WHERE t.GameId = s.GameId);
GO
