/* =============================================================================
   Vault Cracker - register as an SD GAMING title (same shape as DEFUSE)
   Date:    2026-11-05
   Target:  BetAvenue-Dev (used by Development and Staging Games-API)
   Notes:
     - Provider 10013 (FeedingFranzee / SD GAMING). Games-API routes it through
       ProviderEnum.SdGaming and sends ProviderGameID 9 to the game server's
       create-session, which maps it to GameId "vault-cracker"
       (FeedingFranzeeAPI dbo.Games / appsettings.json "Games", Engine Vault).
       Wallet calls come back with GameId 9 and are booked under this row via
       SdGamingProviderId = 10013.
     - GameTypeID 11 (Scratch Cards), like DEFUSE and Mystery Box.
       Enum 725: next free after DEFUSE (724). Games-API's
       Game.DTO/Enums/Games.cs needs a matching VaultCracker = 725 entry.
     - Active for SpinSaga (operator 20050), like the other SD GAMING titles.
     - Re-running is a no-op.
     - Applied to BetAvenue-Dev on 2026-11-06: Game.ID 33639.
   ============================================================================= */

DECLARE @GameID bigint = (SELECT ID FROM Game WHERE ProviderID = 10013 AND ProviderGameID = N'9');

IF @GameID IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Game WHERE ID = @GameID AND ProviderGameCode = N'vault-cracker')
    THROW 50002, 'ProviderGameID 9 is already used by another SD GAMING game - pick a new value here and in appsettings.json.', 1;

IF @GameID IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM Game WHERE Enum = 725)
        THROW 50001, 'Game.Enum 725 is already used by another game - pick a new value here.', 1;

    INSERT INTO Game (Name, IsActive, GameTypeID, ProviderID, GameUrl, Enum, TagID, ProviderGameID, ProviderGameCategory, ProviderGameCode)
    VALUES (N'Vault Cracker', 1, 11, 10013, N'https://vaultcracker.easygamesdemo.co.za/', 725, NULL, N'9', NULL, N'vault-cracker');

    SET @GameID = SCOPE_IDENTITY();
END

IF NOT EXISTS (SELECT 1 FROM GameImages WHERE GameID = @GameID)
    INSERT INTO GameImages (GameID, ImageURL)
    VALUES (@GameID, N'https://vaultcracker.easygamesdemo.co.za/thumbnail.png');

IF NOT EXISTS (SELECT 1 FROM OperatorGame WHERE GameID = @GameID AND OperatorID = 20050)
    INSERT INTO OperatorGame (GameID, Position, OperatorID, IsActive)
    VALUES (@GameID, 1, 20050, 1);

SELECT g.ID, g.Name, g.Enum, g.ProviderGameID, g.GameTypeID, g.GameUrl, i.ImageURL, og.OperatorID, og.IsActive
FROM Game g
LEFT JOIN GameImages i ON i.GameID = g.ID
LEFT JOIN OperatorGame og ON og.GameID = g.ID
WHERE g.ID = @GameID;
