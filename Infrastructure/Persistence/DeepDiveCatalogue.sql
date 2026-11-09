/* =============================================================================
   Deep Dive - register as an SD GAMING title (same shape as Vault Cracker)
   Date:    2026-11-06
   Target:  BetAvenue-Dev (used by Development and Staging Games-API)
   Notes:
     - Provider 10013 (FeedingFranzee / SD GAMING). Games-API routes it through
       ProviderEnum.SdGaming and sends ProviderGameID 10 to the game server's
       create-session, which maps it to GameId "deep-dive"
       (FeedingFranzeeAPI dbo.Games / appsettings.json "Games", Engine Dive).
       Wallet calls come back with GameId 10 and are booked under this row via
       SdGamingProviderId = 10013.
     - GameTypeID 11 (Scratch Cards), like DEFUSE and Vault Cracker.
       Enum 726: next free after Vault Cracker (725). Games-API's
       Game.DTO/Enums/Games.cs needs a matching DeepDive = 726 entry.
     - Active for SpinSaga (operator 20050), like the other SD GAMING titles.
     - Re-running is a no-op.
   ============================================================================= */

DECLARE @GameID bigint = (SELECT ID FROM Game WHERE ProviderID = 10013 AND ProviderGameID = N'10');

IF @GameID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Game WHERE ID = @GameID AND ProviderGameCode = N'deep-dive')
    THROW 50002, 'ProviderGameID 10 is already used by another SD GAMING game - pick a new value here and in appsettings.json.', 1;

IF @GameID IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM Game WHERE Enum = 726)
        THROW 50001, 'Game.Enum 726 is already used by another game - pick a new value here.', 1;

    INSERT INTO Game (Name, IsActive, GameTypeID, ProviderID, GameUrl, Enum, TagID, ProviderGameID, ProviderGameCategory, ProviderGameCode)
    VALUES (N'Deep Dive', 1, 11, 10013, N'https://deepdive.easygamesdemo.co.za/', 726, NULL, N'10', NULL, N'deep-dive');

    SET @GameID = SCOPE_IDENTITY();
END

IF NOT EXISTS (SELECT 1 FROM GameImages WHERE GameID = @GameID)
    INSERT INTO GameImages (GameID, ImageURL)
    VALUES (@GameID, N'https://deepdive.easygamesdemo.co.za/thumbnail.png');

IF NOT EXISTS (SELECT 1 FROM OperatorGame WHERE GameID = @GameID AND OperatorID = 20050)
    INSERT INTO OperatorGame (GameID, Position, OperatorID, IsActive)
    VALUES (@GameID, 1, 20050, 1);

SELECT g.ID, g.Name, g.Enum, g.ProviderGameID, g.GameTypeID, g.GameUrl, i.ImageURL, og.OperatorID, og.IsActive
FROM Game g
LEFT JOIN GameImages i ON i.GameID = g.ID
LEFT JOIN OperatorGame og ON og.GameID = g.ID
WHERE g.ID = @GameID;
