-- Target application database select karo. Existing rows delete/merge nahi honge.
-- API deploy karne se pehle run karo; duplicate data ho toh review karke resolve karna.
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT UserId, BikeId FROM RideFix.UserBikes
        GROUP BY UserId, BikeId HAVING COUNT(*) > 1
    )
        THROW 50001, 'Duplicate garage entries exist. Resolve them before adding the unique index.', 1;

    IF EXISTS (
        SELECT Make, Model, Year FROM RideFix_Customs.MasterBikes
        GROUP BY Make, Model, Year HAVING COUNT(*) > 1
    )
        THROW 50002, 'Duplicate catalog make/model/year combinations exist. Resolve them first.', 1;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID('RideFix.UserBikes') AND name = 'UX_UserBikes_UserId_BikeId'
    )
        CREATE UNIQUE INDEX UX_UserBikes_UserId_BikeId ON RideFix.UserBikes (UserId, BikeId);

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID('RideFix_Customs.MasterBikes') AND name = 'UX_MasterBikes_Make_Model_Year'
    )
        CREATE UNIQUE INDEX UX_MasterBikes_Make_Model_Year ON RideFix_Customs.MasterBikes (Make, Model, Year);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
