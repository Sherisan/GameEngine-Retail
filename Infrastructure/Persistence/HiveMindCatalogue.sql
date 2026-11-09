/* =============================================================================
   Hive Mind - register as an SD GAMING title (same shape as Deep Dive)
   Date:    2026-11-08
   Target:  BetAvenue-Dev (used by Development and Staging Games-API)
   Notes:
     - Provider 10013 (FeedingFranzee / SD GAMING). Games-API routes it through
       ProviderEnum.SdGaming and sends ProviderGameID 12 to the game server's
       create-session, which maps it to GameId "hive-mind"
       (FeedingFranzeeAPI dbo.Games / appsettings.json "Games", Engine Hive).
       Wallet calls come back with GameId 12 and are booked under this row via
       SdGamingProviderId = 10013.
     - GameTypeID 11 (Scratch Cards), like DEFUSE, Vault Cracker and Deep Dive.
       Enum 728: next free after Juice Rush (727). Games-API's
       Game.DTO/Enums/Games.cs needs a matching HiveMind = 728 entry.
     - The lobby image is the frontend's /thumbnail.png: add public/thumbnail.png
       to the Hive Mind frontend before the lobby shows it.
     - Active for SpinSaga (operator 20050), like the other SD GAMING titles.
     - Re-running is a no-op.
   ============================================================================= */

DECLARE @GameID bigint = (SELECT ID FROM Game WHERE ProviderID = 10013 AND ProviderGameID = N'12');

IF @GameID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Game WHERE ID = @GameID AND ProviderGameCode = N'hive-mind')
    THROW 50002, 'ProviderGameID 12 is already used by another SD GAMING game - pick a new value here and in appsettings.json.', 1;

IF @GameID IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM Game WHERE Enum = 728)
        THROW 50001, 'Game.Enum 728 is already used by another game - pick a new value here.', 1;

    INSERT INTO Game (Name, IsActive, GameTypeID, ProviderID, GameUrl, Enum, TagID, ProviderGameID, ProviderGameCategory, ProviderGameCode)
    VALUES (N'Hive Mind', 1, 11, 10013, N'https://hivemind.easygamesdemo.co.za/', 728, NULL, N'12', NULL, N'hive-mind');

    SET @GameID = SCOPE_IDENTITY();
END

IF NOT EXISTS (SELECT 1 FROM GameImages WHERE GameID = @GameID)
    INSERT INTO GameImages (GameID, ImageURL)
    VALUES (@GameID, N'https://hivemind.easygamesdemo.co.za/thumbnail.png');

IF NOT EXISTS (SELECT 1 FROM OperatorGame WHERE GameID = @GameID AND OperatorID = 20050)
    INSERT INTO OperatorGame (GameID, Position, OperatorID, IsActive)
    VALUES (@GameID, 1, 20050, 1);

SELECT g.ID, g.Name, g.Enum, g.ProviderGameID, g.GameTypeID, g.GameUrl, i.ImageURL, og.OperatorID, og.IsActive
FROM Game g
LEFT JOIN GameImages i ON i.GameID = g.ID
LEFT JOIN OperatorGame og ON og.GameID = g.ID
WHERE g.ID = @GameID;
