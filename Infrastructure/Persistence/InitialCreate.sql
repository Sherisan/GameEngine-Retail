IF DB_ID(N'FeedingFranzee') IS NULL
BEGIN
    CREATE DATABASE [FeedingFranzee];
END;
GO

USE [FeedingFranzee];
GO

IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260622113020_InitialCreate'
)
BEGIN
    CREATE TABLE [Rounds] (
        [RoundId] nvarchar(32) NOT NULL,
        [Nonce] bigint NOT NULL,
        [ServerSeed] nvarchar(64) NOT NULL,
        [ServerSeedHash] nvarchar(64) NOT NULL,
        [ClientSeed] nvarchar(64) NOT NULL,
        [CrashMultiplier] decimal(10,2) NOT NULL,
        [Phase] nvarchar(10) NOT NULL,
        [StartedAt] datetime2 NOT NULL,
        [FlyingStartedAt] datetime2 NULL,
        [CrashedAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Rounds] PRIMARY KEY ([RoundId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260622113020_InitialCreate'
)
BEGIN
    CREATE TABLE [Bets] (
        [BetId] nvarchar(32) NOT NULL,
        [RoundId] nvarchar(32) NOT NULL,
        [PlayerId] nvarchar(100) NOT NULL,
        [Username] nvarchar(100) NOT NULL,
        [SlotIndex] int NOT NULL,
        [Amount] decimal(12,2) NOT NULL,
        [Currency] nvarchar(10) NOT NULL,
        [CashedOutAt] decimal(10,2) NULL,
        [WinAmount] decimal(12,2) NULL,
        [AutoCashOutAt] decimal(10,2) NULL,
        [DebitTransactionId] nvarchar(100) NULL,
        [CreditTransactionId] nvarchar(100) NULL,
        [PlacedAt] datetime2 NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Bets] PRIMARY KEY ([BetId]),
        CONSTRAINT [FK_Bets_Rounds_RoundId] FOREIGN KEY ([RoundId]) REFERENCES [Rounds] ([RoundId]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260622113020_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Bets_PlayerId] ON [Bets] ([PlayerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260622113020_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Bets_RoundId] ON [Bets] ([RoundId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260622113020_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Rounds_CrashedAt] ON [Rounds] ([CrashedAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260622113020_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260622113020_InitialCreate', N'8.0.0');
END;
GO

COMMIT;
GO
