-- Platform catalogue rows (BetAvenue database) for the games this API hosts besides FeedingFranzee.
-- Same provider as FeedingFranzee (Provider 10013, Game 14026). Safe to re-run.
--   ProviderGameID — numeric id Games-API sends back to this API at launch; must match "ProviderGameId" in this
--                    API's "Games" config (appsettings.json) and is how Games-API books each bet to the right title.
--   Enum           — GameEnumId on wallet transactions; matches Game.DTO/Enums/Games.cs in Games-API.
-- Games-API also needs "SdGamingProviderId": 10013 in its config.

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM dbo.Game WHERE ProviderID = 10013 AND ProviderGameID = 3)
    INSERT INTO dbo.Game (Name, IsActive, GameTypeID, ProviderID, GameUrl, Enum, Tags, TagID, ProviderGameID, ProviderGameCategory, ProviderGameCode)
    VALUES (N'Margin Call', 1, 1 /* crash */, 10013, N'https://margincall.easygamesdemo.co.za/', 717, NULL, NULL, 3, NULL, N'tradex');

IF NOT EXISTS (SELECT 1 FROM dbo.Game WHERE ProviderID = 10013 AND ProviderGameID = 2)
    INSERT INTO dbo.Game (Name, IsActive, GameTypeID, ProviderID, GameUrl, Enum, Tags, TagID, ProviderGameID, ProviderGameCategory, ProviderGameCode)
    VALUES (N'Mystery Box', 1, 11 /* Scratch Cards */, 10013, N'https://mysterybox.easygamesdemo.co.za/', 716, NULL, NULL, 2, NULL, N'mystery-box');

COMMIT TRANSACTION;

SELECT ID, Name, IsActive, GameTypeID, ProviderID, GameUrl, Enum, ProviderGameID, ProviderGameCode
FROM dbo.Game WHERE ProviderID = 10013 ORDER BY ID;

-- ── Availability: these games are enabled for operator 20050 (SpinSaga) only ─────────────────────────
-- The lobby (GetGameList) lists a game for an operator only if it has an OperatorGame row for that operator
-- and a GameImages row, so no other operator sees them. AssignGamesToOperators skips existing assignments.
EXEC dbo.AssignGamesToOperators @OperatorID = 20050, @GameID = 33519, @position = 1, @IsActive = 1;  -- Margin Call
EXEC dbo.AssignGamesToOperators @OperatorID = 20050, @GameID = 33520, @position = 1, @IsActive = 1;  -- Mystery Box

-- Lobby thumbnails, served by each game's own frontend (public/thumbnail.png).
IF NOT EXISTS (SELECT 1 FROM dbo.GameImages WHERE GameID = 33519)
    INSERT INTO dbo.GameImages (GameID, ImageURL) VALUES (33519, N'https://margincall.easygamesdemo.co.za/thumbnail.png');
IF NOT EXISTS (SELECT 1 FROM dbo.GameImages WHERE GameID = 33520)
    INSERT INTO dbo.GameImages (GameID, ImageURL) VALUES (33520, N'https://mysterybox.easygamesdemo.co.za/thumbnail.png');

SELECT og.OperatorID, og.GameID, g.Name, og.Position, og.IsActive, gi.ImageURL
FROM dbo.OperatorGame og JOIN dbo.Game g ON g.ID = og.GameID LEFT JOIN dbo.GameImages gi ON gi.GameID = g.ID
WHERE og.GameID IN (33519, 33520);
