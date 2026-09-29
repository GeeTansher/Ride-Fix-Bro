-- Application database mein apply karke re-scaffold karo. Koi row delete nahi hoti.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF COL_LENGTH('RideFix.UserBikes', 'IsDeleted') IS NULL
        ALTER TABLE RideFix.UserBikes ADD IsDeleted BIT NOT NULL
            CONSTRAINT DF_UserBikes_IsDeleted DEFAULT (0) WITH VALUES;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- Runtime MI ko UserBikes par SELECT, INSERT, UPDATE aur MasterBikes par SELECT chahiye.
-- Existing Add_Garage_Unique_Indexes.sql bhi apply hona chahiye; duplicate entries mat banao.
