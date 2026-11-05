-- Game Back Office sign-in accounts (api/backoffice/auth, api/backoffice/users). Safe to re-run.
--
-- No seed rows: when this table is empty the API creates the first Admin on startup from
-- BackOffice:BootstrapAdmin:Username / Password (set it once via environment / secrets, sign in,
-- change the password, then remove the setting). Admins manage everyone else from the back office.

USE [FeedingFranzee];
GO

IF OBJECT_ID(N'dbo.BackOfficeUsers') IS NULL
BEGIN
    CREATE TABLE dbo.BackOfficeUsers (
        [UserId]        int IDENTITY(1,1) NOT NULL,
        [Username]      nvarchar(64)  NOT NULL,
        [DisplayName]   nvarchar(100) NOT NULL,
        [PasswordHash]  nvarchar(100) NOT NULL,           -- BCrypt
        [Role]          nvarchar(16)  NOT NULL,           -- Viewer | Editor | Admin
        [Enabled]       bit           NOT NULL CONSTRAINT [DF_BackOfficeUsers_Enabled] DEFAULT 1,
        -- Changes whenever the password, role or enabled flag changes; signed into each token so old tokens stop working.
        [SecurityStamp] nvarchar(32)  NOT NULL,
        [CreatedAt]     datetime2     NOT NULL CONSTRAINT [DF_BackOfficeUsers_CreatedAt] DEFAULT SYSUTCDATETIME(),
        [LastLoginAt]   datetime2     NULL,
        CONSTRAINT [PK_BackOfficeUsers] PRIMARY KEY ([UserId]),
        CONSTRAINT [UQ_BackOfficeUsers_Username] UNIQUE ([Username]),
        CONSTRAINT [CK_BackOfficeUsers_Role] CHECK ([Role] IN (N'Viewer', N'Editor', N'Admin'))
    );
END;
GO
