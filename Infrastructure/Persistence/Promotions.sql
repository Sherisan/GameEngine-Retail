-- Promo tools managed from the Game Back Office (api/backoffice/promotions), run by PromoEngine. Safe to re-run.
--
--   dbo.Promotions      one row per promotion; the type-specific settings are in ConfigJson (the whole Promotion).
--   dbo.PromotionAudit  every change to a promotion (old / new JSON, who, when).
--   dbo.PromoAwards     the promo ledger: every cash payout and free bet. Cash goes to the wallet as a credit with
--                       reference PROMO_<AwardId> (idempotent at the operator), funded from the promotion's Budget.
--   dbo.PromoScores     best qualifying result per player per leaderboard period.

USE [FeedingFranzee];
GO

IF OBJECT_ID(N'dbo.Promotions') IS NULL
BEGIN
    CREATE TABLE dbo.Promotions (
        [PromotionId] int IDENTITY(1,1) NOT NULL,
        [Name]        nvarchar(100) NOT NULL,
        [Type]        nvarchar(32)  NOT NULL,
        [Enabled]     bit           NOT NULL,
        [StartsAt]    datetime2     NULL,
        [EndsAt]      datetime2     NULL,
        [Budget]      decimal(18,2) NOT NULL,
        [ConfigJson]  nvarchar(max) NOT NULL,
        [UpdatedAt]   datetime2     NOT NULL CONSTRAINT [DF_Promotions_UpdatedAt] DEFAULT SYSUTCDATETIME(),
        [UpdatedBy]   nvarchar(100) NOT NULL,
        CONSTRAINT [PK_Promotions] PRIMARY KEY ([PromotionId])
    );
END;
GO

IF OBJECT_ID(N'dbo.PromotionAudit') IS NULL
BEGIN
    CREATE TABLE dbo.PromotionAudit (
        [AuditId]     bigint IDENTITY(1,1) NOT NULL,
        [PromotionId] int           NOT NULL,
        [OldValue]    nvarchar(max) NULL,
        [NewValue]    nvarchar(max) NOT NULL,
        [ChangedBy]   nvarchar(100) NOT NULL,
        [ChangedAt]   datetime2     NOT NULL CONSTRAINT [DF_PromotionAudit_ChangedAt] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_PromotionAudit] PRIMARY KEY ([AuditId])
    );
    CREATE INDEX [IX_PromotionAudit_Promotion] ON dbo.PromotionAudit ([PromotionId], [ChangedAt] DESC);
END;
GO

IF OBJECT_ID(N'dbo.PromoAwards') IS NULL
BEGIN
    CREATE TABLE dbo.PromoAwards (
        [AwardId]       char(32)      NOT NULL,
        [PromotionId]   int           NOT NULL,
        [GameId]        nvarchar(64)  NOT NULL,
        [PlayerId]      nvarchar(64)  NOT NULL,
        [Username]      nvarchar(100) NOT NULL,
        [Currency]      nvarchar(8)   NOT NULL,
        [Kind]          nvarchar(16)  NOT NULL,           -- Cash | FreeBet
        [Amount]        decimal(18,2) NOT NULL,
        [Reason]        nvarchar(200) NOT NULL,
        [Status]        nvarchar(16)  NOT NULL,           -- Pending | Paid | Failed | Available | InPlay | Used | Expired | Cancelled
        [CreatedAt]     datetime2     NOT NULL,
        [SettledAt]     datetime2     NULL,
        [ExpiresAt]     datetime2     NULL,
        [RoundId]       nvarchar(64)  NULL,
        [WinAmount]     decimal(18,2) NULL,
        [ParentAwardId] char(32)      NULL,
        [Error]         nvarchar(500) NULL,
        [Attempts]      int           NOT NULL CONSTRAINT [DF_PromoAwards_Attempts] DEFAULT 0,
        CONSTRAINT [PK_PromoAwards] PRIMARY KEY ([AwardId])
    );
    CREATE INDEX [IX_PromoAwards_Promotion] ON dbo.PromoAwards ([PromotionId], [CreatedAt] DESC) INCLUDE ([Kind], [Status], [Amount], [RoundId], [ParentAwardId]);
    CREATE INDEX [IX_PromoAwards_Player] ON dbo.PromoAwards ([PlayerId], [Status]) INCLUDE ([PromotionId], [Kind], [ExpiresAt]);
END;
GO

IF OBJECT_ID(N'dbo.PromoScores') IS NULL
BEGIN
    CREATE TABLE dbo.PromoScores (
        [PromotionId] int           NOT NULL,
        [PeriodKey]   nvarchar(16)  NOT NULL,             -- all | 2026-11-03 | 2026-W45
        [PlayerId]    nvarchar(64)  NOT NULL,
        [Username]    nvarchar(100) NOT NULL,
        [Currency]    nvarchar(8)   NOT NULL,
        [GameId]      nvarchar(64)  NOT NULL,
        [Score]       decimal(18,2) NOT NULL,
        [Stake]       decimal(18,2) NOT NULL,
        [AchievedAt]  datetime2     NOT NULL,
        CONSTRAINT [PK_PromoScores] PRIMARY KEY ([PromotionId], [PeriodKey], [PlayerId])
    );
    CREATE INDEX [IX_PromoScores_Rank] ON dbo.PromoScores ([PromotionId], [PeriodKey], [Score] DESC, [AchievedAt]);
END;
GO

-- Added for the card, roulette and blackjack promotions: a player's running state in a promotion
-- (buy-X card count, streak, collected symbols, per-day caps). One row per promotion and player.
IF OBJECT_ID(N'dbo.PromoProgress') IS NULL
BEGIN
    CREATE TABLE dbo.PromoProgress (
        [PromotionId] int           NOT NULL,
        [PlayerId]    nvarchar(64)  NOT NULL,
        [Count]       int           NOT NULL CONSTRAINT [DF_PromoProgress_Count] DEFAULT 0,
        [Streak]      int           NOT NULL CONSTRAINT [DF_PromoProgress_Streak] DEFAULT 0,
        [Completed]   int           NOT NULL CONSTRAINT [DF_PromoProgress_Completed] DEFAULT 0,
        [Data]        nvarchar(400) NOT NULL CONSTRAINT [DF_PromoProgress_Data] DEFAULT N'',
        [LastAt]      datetime2     NULL,
        [DayKey]      nvarchar(10)  NOT NULL CONSTRAINT [DF_PromoProgress_DayKey] DEFAULT N'',
        [DayCount]    int           NOT NULL CONSTRAINT [DF_PromoProgress_DayCount] DEFAULT 0,
        CONSTRAINT [PK_PromoProgress] PRIMARY KEY ([PromotionId], [PlayerId])
    );
END;
GO
