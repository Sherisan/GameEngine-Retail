-- Shared multi-game wallet ledger (see SqlWalletLedger.cs). Run once per environment
-- before enabling any game's ServerKey in the "Games" config.
USE [FeedingFranzee];
GO

IF OBJECT_ID(N'dbo.WalletTransactions') IS NULL
BEGIN
    CREATE TABLE dbo.WalletTransactions (
        [Id]                  bigint IDENTITY(1,1) NOT NULL,
        [GameId]              nvarchar(50)  NOT NULL,
        [ExternalTxId]        nvarchar(64)  NOT NULL,
        [Type]                nvarchar(10)  NOT NULL,
        [Status]              nvarchar(12)  NOT NULL,
        [PlayerId]            nvarchar(100) NOT NULL,
        [Currency]            nvarchar(10)  NOT NULL,
        [RoundId]             nvarchar(64)  NOT NULL,
        [Amount]              decimal(12,2) NOT NULL,
        [Reference]           nvarchar(100) NOT NULL,
        [RelatedExternalTxId] nvarchar(64)  NULL,
        [Error]               nvarchar(500) NULL,
        [CreatedAt]           datetime2     NOT NULL,
        [CompletedAt]         datetime2     NULL,
        CONSTRAINT [PK_WalletTransactions] PRIMARY KEY ([Id]),
        CONSTRAINT [UQ_WalletTransactions_Game_Tx] UNIQUE ([GameId], [ExternalTxId])
    );

    CREATE INDEX [IX_WalletTransactions_Game_Player_Round]
        ON dbo.WalletTransactions ([GameId], [PlayerId], [RoundId]);
END;
GO
