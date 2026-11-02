/* =============================================================================
   European Roulette - register as an SD GAMING title (same shape as Message in a Bottle)
   Date:    2026-11-01
   Target:  BetAvenue-Dev (used by Development and Staging Games-API)
   Notes:
     - Provider 10013 (FeedingFranzee / SD GAMING). Games-API routes it through
       ProviderEnum.SdGaming and sends ProviderGameID 6 to the game server's
       create-session, which maps it to GameId "roulette"
       (FeedingFranzeeAPI appsettings.json "Games"). Wallet calls come back with
       GameId 6 and are booked under this row via SdGamingProviderId = 10013.
     - GameTypeID 10, like the RNG "Roulette X" (3 is live-dealer casino).
       Enum 720: next after Message in a Bottle (719) and Comet Rider (718).
     - Active for SpinSaga (operator 20050), like the other SD GAMING titles.
     - Re-running is a no-op.
   ============================================================================= */

DECLARE @GameID bigint = (SELECT ID FROM Game WHERE ProviderID = 10013 AND ProviderGameID = N'6');

IF @GameID IS NULL
BEGIN
    INSERT INTO Game (Name, IsActive, GameTypeID, ProviderID, GameUrl, Enum, TagID, ProviderGameID, ProviderGameCategory, ProviderGameCode)
    VALUES (N'European Roulette', 1, 10, 10013, N'https://roulette.easygamesdemo.co.za/', 720, NULL, N'6', NULL, N'roulette');

    SET @GameID = SCOPE_IDENTITY();
END

IF NOT EXISTS (SELECT 1 FROM GameImages WHERE GameID = @GameID)
    INSERT INTO GameImages (GameID, ImageURL)
    VALUES (@GameID, N'https://roulette.easygamesdemo.co.za/thumbnail.png');

IF NOT EXISTS (SELECT 1 FROM OperatorGame WHERE GameID = @GameID AND OperatorID = 20050)
    INSERT INTO OperatorGame (GameID, Position, OperatorID, IsActive)
    VALUES (@GameID, 1, 20050, 1);

SELECT g.ID, g.Name, g.Enum, g.ProviderGameID, g.GameTypeID, g.GameUrl, i.ImageURL, og.OperatorID, og.IsActive
FROM Game g
LEFT JOIN GameImages i ON i.GameID = g.ID
LEFT JOIN OperatorGame og ON og.GameID = g.ID
WHERE g.ID = @GameID;
