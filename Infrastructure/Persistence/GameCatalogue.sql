-- Game catalogue managed from the Game Back Office (api/backoffice/games). Run after GameConfiguration.sql
-- (it uses dbo.GameConfigAudit for the change history). Safe to re-run.
--
-- No seed rows here: on startup the API copies every game from the "Games" array in appsettings.json
-- that this table doesn't have yet. Rows already here are never overwritten — from then on the
-- back office is the source of truth, and appsettings is only the fallback if the database is down.

USE [FeedingFranzee];
GO

IF OBJECT_ID(N'dbo.Games') IS NULL
BEGIN
    CREATE TABLE dbo.Games (
        [GameId]         nvarchar(64)  NOT NULL,
        [ProviderGameId] bigint        NULL,
        [Name]           nvarchar(100) NOT NULL,
        [Type]           nvarchar(20)  NOT NULL,
        [Engine]         nvarchar(20)  NOT NULL,
        [LaunchUrl]      nvarchar(400) NOT NULL CONSTRAINT [DF_Games_LaunchUrl] DEFAULT N'',
        [ServerKey]      nvarchar(200) NOT NULL CONSTRAINT [DF_Games_ServerKey] DEFAULT N'',
        [MinBet]         decimal(18,2) NOT NULL,
        [MaxBet]         decimal(18,2) NOT NULL,
        [MaxWin]         decimal(18,2) NOT NULL,
        [Enabled]        bit           NOT NULL,
        -- Engine-specific blocks as JSON (same shape as appsettings "Games"): RngSettings, LiveCrashSettings, RouletteSettings, TradingSettings, BlackjackSettings.
        [RngJson]        nvarchar(max) NULL,
        [LiveJson]       nvarchar(max) NULL,
        [RouletteJson]   nvarchar(max) NULL,
        [TradingJson]    nvarchar(max) NULL,
        [BlackjackJson]  nvarchar(max) NULL,
        [SortOrder]      int           NOT NULL CONSTRAINT [DF_Games_SortOrder] DEFAULT 0,
        [UpdatedAt]      datetime2     NOT NULL CONSTRAINT [DF_Games_UpdatedAt] DEFAULT SYSUTCDATETIME(),
        [UpdatedBy]      nvarchar(100) NOT NULL,
        CONSTRAINT [PK_Games] PRIMARY KEY ([GameId]),
        CONSTRAINT [CK_Games_Limits] CHECK ([MinBet] > 0 AND [MaxBet] >= [MinBet] AND [MaxWin] > 0)
    );
    CREATE UNIQUE INDEX [UX_Games_ProviderGameId] ON dbo.Games ([ProviderGameId]) WHERE [ProviderGameId] IS NOT NULL;
END;
GO

-- Added for TradeX (Engine MarginCall): markets, commission, spread and round timing.
-- On the next startup the API fills it in once from the appsettings "MarginCall" section.
IF COL_LENGTH(N'dbo.Games', N'TradingJson') IS NULL
    ALTER TABLE dbo.Games ADD [TradingJson] nvarchar(max) NULL;
GO

-- Added for Blackjack (Engine Blackjack): table rules (decks, soft 17, blackjack payout, splitting).
IF COL_LENGTH(N'dbo.Games', N'BlackjackJson') IS NULL
    ALTER TABLE dbo.Games ADD [BlackjackJson] nvarchar(max) NULL;
GO
