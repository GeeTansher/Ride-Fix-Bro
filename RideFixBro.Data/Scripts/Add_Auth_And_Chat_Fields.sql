-- SSMS mein RideFix Bro wala database select karke run karna.
-- Existing rows preserve honge. Legacy identity/message-order values khud guess nahi karte.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

BEGIN TRY
    BEGIN TRANSACTION;

    -- 1. Google/Supabase login ko apne existing INT User Id se link karna.
    IF COL_LENGTH('RideFix.Users', 'SupabaseUserId') IS NULL
        ALTER TABLE RideFix.Users ADD SupabaseUserId UNIQUEIDENTIFIER NULL;

    -- Naya column isi batch mein add hua hai; EXEC uske baad statement compile karata hai.
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID('RideFix.Users') AND name = 'UX_Users_SupabaseUserId'
    )
        EXEC(N'CREATE UNIQUE INDEX UX_Users_SupabaseUserId
               ON RideFix.Users (SupabaseUserId)
               WHERE SupabaseUserId IS NOT NULL;');

    IF OBJECT_ID('RideFix.CK_Users_SupabaseUserId', 'C') IS NULL
        EXEC(N'ALTER TABLE RideFix.Users WITH CHECK
               ADD CONSTRAINT CK_Users_SupabaseUserId CHECK (
                   SupabaseUserId IS NULL
                   OR SupabaseUserId <> ''00000000-0000-0000-0000-000000000000''
               );');

    IF OBJECT_ID('RideFix.CK_Users_Role', 'C') IS NULL
        ALTER TABLE RideFix.Users WITH CHECK
        ADD CONSTRAINT CK_Users_Role CHECK (
            Role COLLATE Latin1_General_100_BIN2 IN ('User', 'Admin')
        );

    -- 2. Tool request ka text null ho sakta hai; uska complete payload alag rahega.
    IF COL_LENGTH('RideFix.Messages', 'TurnNumber') IS NULL
        ALTER TABLE RideFix.Messages ADD TurnNumber INT NULL;

    IF COL_LENGTH('RideFix.Messages', 'SequenceNumber') IS NULL
        ALTER TABLE RideFix.Messages ADD SequenceNumber INT NULL;

    IF COL_LENGTH('RideFix.Messages', 'PayloadJson') IS NULL
        ALTER TABLE RideFix.Messages ADD PayloadJson NVARCHAR(MAX) NULL;

    IF EXISTS (
        SELECT 1 FROM sys.columns
        WHERE object_id = OBJECT_ID('RideFix.Messages') AND name = 'Content' AND is_nullable = 0
    )
        ALTER TABLE RideFix.Messages ALTER COLUMN Content NVARCHAR(MAX) NULL;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID('RideFix.Messages') AND name = 'UX_Messages_Session_Sequence'
    )
        EXEC(N'CREATE UNIQUE INDEX UX_Messages_Session_Sequence
               ON RideFix.Messages (ChatSessionId, SequenceNumber)
               WHERE SequenceNumber IS NOT NULL;');

    IF OBJECT_ID('RideFix.CK_Messages_Order', 'C') IS NULL
        EXEC(N'ALTER TABLE RideFix.Messages WITH CHECK
               ADD CONSTRAINT CK_Messages_Order CHECK (
                   (TurnNumber IS NULL AND SequenceNumber IS NULL)
                   OR (TurnNumber IS NOT NULL AND SequenceNumber IS NOT NULL
                       AND TurnNumber > 0 AND SequenceNumber > 0)
               );');

    IF OBJECT_ID('RideFix.CK_Messages_PayloadJson', 'C') IS NULL
        EXEC(N'ALTER TABLE RideFix.Messages WITH CHECK
               ADD CONSTRAINT CK_Messages_PayloadJson
               CHECK (PayloadJson IS NULL OR ISJSON(PayloadJson) = 1);');

    IF OBJECT_ID('RideFix.CK_Messages_Content', 'C') IS NULL
        EXEC(N'ALTER TABLE RideFix.Messages WITH CHECK
               ADD CONSTRAINT CK_Messages_Content
               CHECK (Content IS NOT NULL OR PayloadJson IS NOT NULL);');

    IF OBJECT_ID('RideFix.CK_Messages_Role', 'C') IS NULL
        ALTER TABLE RideFix.Messages WITH CHECK
        ADD CONSTRAINT CK_Messages_Role CHECK (
            Role COLLATE Latin1_General_100_BIN2 IN ('User', 'Assistant', 'Tool', 'System')
        );

    -- 3. Chat ka UserId aur selected garage bike ka owner same hona chahiye.
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID('RideFix.UserBikes') AND name = 'UX_UserBikes_Id_UserId'
    )
        CREATE UNIQUE INDEX UX_UserBikes_Id_UserId ON RideFix.UserBikes (Id, UserId);

    IF OBJECT_ID('RideFix.FK_ChatSessions_UserBikeOwner', 'F') IS NULL
        ALTER TABLE RideFix.ChatSessions WITH CHECK
        ADD CONSTRAINT FK_ChatSessions_UserBikeOwner
        FOREIGN KEY (UserBikeId, UserId) REFERENCES RideFix.UserBikes (Id, UserId);

    -- Composite FK purane single-column FK ki jagah owner bhi validate karta hai.
    IF OBJECT_ID('RideFix.FK_ChatSessions_UserBikes', 'F') IS NOT NULL
        ALTER TABLE RideFix.ChatSessions DROP CONSTRAINT FK_ChatSessions_UserBikes;

    COMMIT TRANSACTION;
    PRINT 'Auth/chat schema updated. Existing rows preserved; re-scaffold the Data project.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
