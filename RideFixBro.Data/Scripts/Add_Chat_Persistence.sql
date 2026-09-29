-- Target application database mein run karke ChatSessions aur Users re-scaffold karo.
-- Existing chats/messages delete nahi honge. Memory-only chats is script se migrate nahi hote.
-- Existing ChatSessions.Id hi API aur Messages ka stable chat ID rahega.
-- Koi extra ID column ya naya index nahi; existing indexes/unique constraints bhi nahi hatenge.
-- Stored chat mein UserBikeId NULL = explicitly selected General; alag IsGeneral column redundant hai.
-- API ko new chat par explicit General/bike selection aur existing chat par owner check karna hoga.
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

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

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- Runtime MI ko ChatSessions par SELECT/INSERT aur Messages par SELECT/INSERT chahiye.
-- Text + tool payloads persist honge; photos/Blob storage is change ka part nahi hain.
