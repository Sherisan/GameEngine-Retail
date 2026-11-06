/* =============================================================================
   DEFUSE - game server catalogue row (Engine Defuse)
   Date:    2026-11-05
   Target:  FeedingFranzee (dbo.Games, see GameCatalogue.sql)
   Notes:
     - Same values as the "defuse" entry in appsettings.json "Games". The API also copies
       that entry in on startup if the row is missing; this creates it ahead of the deploy.
       An existing row is never overwritten (the back office owns it from then on).
     - ProviderGameId 8 matches Game.ProviderGameID in BetAvenue (Game.ID 33638, Enum 724,
       DefuseCatalogue.sql).
     - Safe before the API build with Engine Defuse is deployed: an older build logs
       "dbo.Games row defuse is invalid" and skips the row; the other games load as usual.
     - No engine JSON blocks: Engine Defuse has a fixed 97% ladder.
     - MaxWin must be >= MaxBet x 5.82 (500 x 5.82 = 2910) or the API rejects the row.
     - Re-running is a no-op.
     - Applied to FeedingFranzee (Dev) on 2026-11-06.
   ============================================================================= */

USE [FeedingFranzee];
GO

IF EXISTS (SELECT 1 FROM dbo.Games WHERE ProviderGameId = 8 AND GameId <> N'defuse')
    THROW 50003, 'ProviderGameId 8 is already used by another game in dbo.Games.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Games WHERE GameId = N'defuse')
    INSERT INTO dbo.Games
        (GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, ServerKey, MinBet, MaxBet, MaxWin, Enabled,
         RngJson, LiveJson, RouletteJson, TradingJson, BlackjackJson, SortOrder, UpdatedBy)
    VALUES
        (N'defuse', 8, N'DEFUSE', N'Scratch', N'Defuse', N'https://defuse.easygamesdemo.co.za/', N'', 1.00, 500.00, 5000.00, 1,
         NULL, NULL, NULL, NULL, NULL, (SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM dbo.Games), N'onboarding');

SELECT GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, MinBet, MaxBet, MaxWin, Enabled, SortOrder, UpdatedAt, UpdatedBy
FROM dbo.Games WHERE GameId = N'defuse';
