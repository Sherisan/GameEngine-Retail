/* =============================================================================
   Hive Mind - game server catalogue row (Engine Hive)
   Date:    2026-11-08
   Target:  FeedingFranzee (dbo.Games, see GameCatalogue.sql)
   Notes:
     - Same values as the "hive-mind" entry in appsettings.json "Games". The API also
       copies that entry in on startup if the row is missing; this creates it ahead of the
       deploy. An existing row is never overwritten (the back office owns it from then on).
     - ProviderGameId 12 matches Game.ProviderGameID in BetAvenue (Enum 728,
       HiveMindCatalogue.sql).
     - Safe before the API build with Engine Hive is deployed: an older build logs
       "dbo.Games row hive-mind is invalid" on every config refresh and skips the row;
       the other games load as usual.
     - No engine JSON blocks: Engine Hive prices every pick from the board (HiveRules).
     - MaxWin must be >= MaxBet x 500 (10 x 500 = 5000) or the API rejects the row.
     - Re-running is a no-op.
   ============================================================================= */

USE [FeedingFranzee];
GO

IF EXISTS (SELECT 1 FROM dbo.Games WHERE ProviderGameId = 12 AND GameId <> N'hive-mind')
    THROW 50003, 'ProviderGameId 12 is already used by another game in dbo.Games.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Games WHERE GameId = N'hive-mind')
    INSERT INTO dbo.Games
        (GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, ServerKey, MinBet, MaxBet, MaxWin, Enabled,
         RngJson, LiveJson, RouletteJson, TradingJson, BlackjackJson, SortOrder, UpdatedBy)
    VALUES
        (N'hive-mind', 12, N'Hive Mind', N'Scratch', N'Hive', N'https://hivemind.easygamesdemo.co.za/', N'', 1.00, 10.00, 5000.00, 1,
         NULL, NULL, NULL, NULL, NULL, (SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM dbo.Games), N'onboarding');

SELECT GameId, ProviderGameId, Name, Type, Engine, LaunchUrl, MinBet, MaxBet, MaxWin, Enabled, SortOrder, UpdatedAt, UpdatedBy
FROM dbo.Games WHERE GameId = N'hive-mind';
