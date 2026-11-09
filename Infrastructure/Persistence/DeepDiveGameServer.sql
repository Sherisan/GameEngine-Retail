/* =============================================================================
   Deep Dive - game server catalogue row (Engine Dive)
   Date:    2026-11-06
   Target:  FeedingFranzee (dbo.Games, see GameCatalogue.sql)
   Notes:
     - Same values as the "deep-dive" entry in appsettings.json "Games". The API also
       copies that entry in on startup if the row is missing; this creates it ahead of the
       deploy. An existing row is never overwritten (the back office owns it from then on).
     - ProviderGameId 10 matches Game.ProviderGameID in BetAvenue (Enum 726,
       DeepDiveCatalogue.sql).
     - Safe before the API build with Engine Dive is deployed: an older build logs
       "dbo.Games row deep-dive is invalid" and skips the row; the other games load as usual.
     - No engine JSON blocks: Engine Dive has a fixed 97% ladder.
     - MaxWin must be >= MaxBet x 24.86 (200 x 24.86 = 4972) or the API rejects the row.
     - Re-running is a no-op.
   ============================================================================= */

USE [FeedingFranzee];
GO

IF EXISTS (SELECT 1 FROM dbo.Games WHERE ProviderGameId = 10 AND GameId <> N'deep-dive')
    THROW 50003, 'ProviderGameId 10 is already used by another game in dbo.Games.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Games WHERE GameId = N'deep-dive')
    INSERT INTO dbo.Games
        (GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, ServerKey, MinBet, MaxBet, MaxWin, Enabled,
         RngJson, LiveJson, RouletteJson, TradingJson, BlackjackJson, SortOrder, UpdatedBy)
    VALUES
        (N'deep-dive', 10, N'Deep Dive', N'Scratch', N'Dive', N'https://deepdive.easygamesdemo.co.za/', N'', 1.00, 200.00, 5000.00, 1,
         NULL, NULL, NULL, NULL, NULL, (SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM dbo.Games), N'onboarding');

SELECT GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, MinBet, MaxBet, MaxWin, Enabled, SortOrder, UpdatedAt, UpdatedBy
FROM dbo.Games WHERE GameId = N'deep-dive';
