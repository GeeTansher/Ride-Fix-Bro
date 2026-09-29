-- Target application database mein run karke MasterBikes re-scaffold karo.
-- ManualKey ek verified manual/document set ko identify karta hai, bike ke display name ko nahi.
-- Ye hamari chosen mapping string hai; Qdrant collection/point ID ya auto-generated key nahi.
-- Same manual multiple years cover kare toh sirf verified catalog rows ko same key assign karo.
-- NULL = manual unavailable. Mapping sirf catalog entry hone se automatically nahi banti.
-- Sirf nullable column aur non-blank CHECK constraint add hote hain; koi naya index nahi.
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH('RideFix_Customs.MasterBikes', 'ManualKey') IS NULL
        ALTER TABLE RideFix_Customs.MasterBikes ADD ManualKey NVARCHAR(128) NULL;

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id = OBJECT_ID('RideFix_Customs.MasterBikes')
            AND name = 'CK_MasterBikes_ManualKey'
    )
        EXEC(N'ALTER TABLE RideFix_Customs.MasterBikes WITH CHECK
            ADD CONSTRAINT CK_MasterBikes_ManualKey
            CHECK (ManualKey IS NULL OR LEN(LTRIM(RTRIM(ManualKey))) > 0);');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- Qdrant chunks par isi exact, case-sensitive string ka manual_key payload hona chahiye.
-- Pehle source manual ka model/year verify karo; neeche wala example jaan-boojhkar execute nahi hota.
UPDATE RideFix_Customs.MasterBikes SET ManualKey = N'harley-x440-2024-owner-v1'
WHERE Make = N'Harley-Davidson' AND Model = N'X440' AND Year = 2024;
