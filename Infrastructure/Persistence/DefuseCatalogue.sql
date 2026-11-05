-- Platform catalogue row (BetAvenue database) for DEFUSE, same shape as Margin Call / Mystery Box
-- (BetAvenueCatalogue.sql). Provider 10013 (FeedingFranzee / SD GAMING). Safe to re-run.
--   ProviderGameID — 8: the numeric id Games-API sends back to this API at launch; matches "ProviderGameId" of the
--                    "defuse" entry in this API's "Games" config (appsettings.json, Engine Defuse).
--   Enum           — 724, next free after Arctic Appetite (723): GameEnumId on wallet transactions. Games-API needs a
--                    matching Defuse = 724 in Game.DTO/Enums/Games.cs.
--   GameTypeID     — 11 (Scratch Cards), like Mystery Box: an instant-win game.
-- Games-API also needs "SdGamingProviderId": 10013 in its config (already there for the other SD GAMING titles).
-- Applied to BetAvenue-Dev on 2026-11-05: Game.ID 33638.

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF EXISTS (SELECT 1 FROM dbo.Game WHERE ProviderID = 10013 AND ProviderGameID = 8 AND ProviderGameCode <> N'defuse')
    THROW 50002, 'ProviderGameID 8 is already used by another SD GAMING game - pick a new value here and in appsettings.json.', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.Game WHERE ProviderID = 10013 AND ProviderGameID = 8)
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.Game WHERE Enum = 724)
        THROW 50001, 'Game.Enum 724 is already used by another game - pick a new value here.', 1;

    INSERT INTO dbo.Game (Name, IsActive, GameTypeID, ProviderID, GameUrl, Enum, Tags, TagID, ProviderGameID, ProviderGameCategory, ProviderGameCode)
    VALUES (N'DEFUSE', 1, 11 /* Scratch Cards */, 10013, N'https://defuse.easygamesdemo.co.za/', 724, NULL, NULL, 8, NULL, N'defuse');
END

COMMIT TRANSACTION;

DECLARE @GameID bigint = (SELECT ID FROM dbo.Game WHERE ProviderID = 10013 AND ProviderGameID = 8);

SELECT ID, Name, IsActive, GameTypeID, ProviderID, GameUrl, Enum, ProviderGameID, ProviderGameCode
FROM dbo.Game WHERE ID = @GameID;

-- ── Availability: enabled for operator 20050 (SpinSaga) only ────────────────────────────────────────
-- The lobby (GetGameList) lists a game for an operator only if it has an OperatorGame row for that operator
-- and a GameImages row, so no other operator sees it. AssignGamesToOperators skips existing assignments.
EXEC dbo.AssignGamesToOperators @OperatorID = 20050, @GameID = @GameID, @position = 1, @IsActive = 1;  -- DEFUSE

-- Lobby thumbnail, served by the game's own frontend (public/thumbnail.png).
IF NOT EXISTS (SELECT 1 FROM dbo.GameImages WHERE GameID = @GameID)
    INSERT INTO dbo.GameImages (GameID, ImageURL) VALUES (@GameID, N'https://defuse.easygamesdemo.co.za/thumbnail.png');

SELECT og.OperatorID, og.GameID, g.Name, og.Position, og.IsActive, gi.ImageURL
FROM dbo.OperatorGame og JOIN dbo.Game g ON g.ID = og.GameID LEFT JOIN dbo.GameImages gi ON gi.GameID = g.ID
WHERE og.GameID = @GameID;
