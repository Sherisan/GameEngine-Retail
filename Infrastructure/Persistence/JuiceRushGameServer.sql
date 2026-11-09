/* =============================================================================
   Juice Rush - game server catalogue row (Engine Fruit)
   Date:    2026-11-08
   Target:  FeedingFranzee (dbo.Games, see GameCatalogue.sql)
   Notes:
     - Same values as the "juice-rush" entry in appsettings.json "Games". The API also
       copies that entry in on startup if the row is missing; this creates it ahead of the
       deploy. An existing row is never overwritten (the back office owns it from then on).
     - ProviderGameId 11 matches Game.ProviderGameID in BetAvenue (Enum 727,
       JuiceRushCatalogue.sql).
     - Safe before the API build with Engine Fruit is deployed: an older build logs
       "dbo.Games row juice-rush is invalid" on every config refresh and skips the row;
       the other games load as usual.
     - No engine JSON blocks: Engine Fruit has fixed odds and a 97% RTP (FruitRules).
     - MaxWin must be >= MaxBet x 1000 (5 x 1000 = 5000) or the API rejects the row.
     - Re-running is a no-op.
   ============================================================================= */

USE [FeedingFranzee];
GO

IF EXISTS (SELECT 1 FROM dbo.Games WHERE ProviderGameId = 11 AND GameId <> N'juice-rush')
    THROW 50003, 'ProviderGameId 11 is already used by another game in dbo.Games.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Games WHERE GameId = N'juice-rush')
    INSERT INTO dbo.Games
        (GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, ServerKey, MinBet, MaxBet, MaxWin, Enabled,
         RngJson, LiveJson, RouletteJson, TradingJson, BlackjackJson, SortOrder, UpdatedBy)
    VALUES
        (N'juice-rush', 11, N'Juice Rush', N'Scratch', N'Fruit', N'https://juicerush.easygamesdemo.co.za/', N'', 1.00, 5.00, 5000.00, 1,
         NULL, NULL, NULL, NULL, NULL, (SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM dbo.Games), N'onboarding');

SELECT GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, MinBet, MaxBet, MaxWin, Enabled, SortOrder, UpdatedAt, UpdatedBy
FROM dbo.Games WHERE GameId = N'juice-rush';
