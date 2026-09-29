-- Target application database mein run karke ChatSessions, Messages aur Users re-scaffold karo.
-- Existing chats/messages delete nahi honge. Memory-only chats is script se migrate nahi hote.
-- SessionId API wala public ID hai; numeric Id existing message FK ke liye rahega.
-- Stored chat mein UserBikeId NULL = explicitly selected General; alag IsGeneral column redundant hai.
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('RideFix.ChatSessions', 'SessionId') IS NULL
        ALTER TABLE RideFix.ChatSessions ADD SessionId NVARCHAR(128) NOT NULL
            CONSTRAINT DF_ChatSessions_SessionId DEFAULT (CONVERT(NVARCHAR(36), NEWID())) WITH VALUES;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID('RideFix.ChatSessions') AND name = 'UX_ChatSessions_UserId_SessionId'
    )
        EXEC(N'CREATE UNIQUE INDEX UX_ChatSessions_UserId_SessionId
            ON RideFix.ChatSessions (UserId, SessionId);');

    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID('RideFix.ChatSessions')
            AND name = 'UserBikeId' AND is_nullable = 0
    )
    BEGIN
        IF EXISTS (
            SELECT 1 FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('RideFix.ChatSessions')
                AND name = 'FK_ChatSessions_UserBikeOwner'
        )
            ALTER TABLE RideFix.ChatSessions DROP CONSTRAINT FK_ChatSessions_UserBikeOwner;

        ALTER TABLE RideFix.ChatSessions ALTER COLUMN UserBikeId INT NULL;
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID('RideFix.ChatSessions')
            AND name = 'FK_ChatSessions_UserBikeOwner'
    )
        ALTER TABLE RideFix.ChatSessions WITH CHECK ADD CONSTRAINT FK_ChatSessions_UserBikeOwner
            FOREIGN KEY (UserBikeId, UserId) REFERENCES RideFix.UserBikes (Id, UserId);

    -- General ke NULL bike par composite FK user validate nahi karega; direct user FK zaroori hai.
    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_keys
        WHERE parent_object_id = OBJECT_ID('RideFix.ChatSessions')
            AND name = 'FK_ChatSessions_Users'
    )
        ALTER TABLE RideFix.ChatSessions WITH CHECK ADD CONSTRAINT FK_ChatSessions_Users
            FOREIGN KEY (UserId) REFERENCES RideFix.Users (Id);

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID('RideFix.Messages') AND name = 'IX_Messages_Session_Turn'
    )
        CREATE INDEX IX_Messages_Session_Turn
            ON RideFix.Messages (ChatSessionId, TurnNumber, SequenceNumber);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- Runtime MI ko ChatSessions par SELECT/INSERT aur Messages par SELECT/INSERT chahiye.
-- Text + tool payloads persist honge; photos/Blob storage is change ka part nahi hain.
