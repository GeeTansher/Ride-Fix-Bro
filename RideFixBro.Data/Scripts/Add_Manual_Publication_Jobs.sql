-- Existing application database mein run karo, phir ManualPublicationJobs + context re-scaffold karo.
-- Generated entities assistant edit nahi karega. Add_Initial_Tables.sql dobara run mat karna.
-- Ye script chat Title/RequestId draft se independent hai; existing chat/catalog rows unchanged rahengi.
--
-- Job Id hi Qdrant upload revision banegi. CompletedChunks durable checkpoint hai.
-- ChunksJson mein bounded extracted TEXT rahega, raw PDF/images nahi.
-- Success/failure par text clear hoga; Queued/Processing jobs restart ke baad continue ho sakti hain.
-- One active job per collection/manual key prevents duplicate simultaneous publication.
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'RideFix.Users', N'U') IS NULL
        OR OBJECT_ID(N'RideFix_Customs.MasterBikes', N'U') IS NULL
        THROW 50001, 'Select the existing RideFix application database before running this script.', 1;

    IF OBJECT_ID(N'RideFix.ManualPublicationJobs', N'U') IS NULL
    BEGIN
        CREATE TABLE RideFix.ManualPublicationJobs
        (
            Id UNIQUEIDENTIFIER NOT NULL
                CONSTRAINT DF_ManualPublicationJobs_Id DEFAULT NEWID()
                CONSTRAINT PK_ManualPublicationJobs PRIMARY KEY,
            SubmittedByUserId INT NOT NULL,
            Make NVARCHAR(100) NOT NULL,
            Model NVARCHAR(100) NOT NULL,
            Year INT NOT NULL,
            ManualKey NVARCHAR(128) NOT NULL,
            CollectionName NVARCHAR(255) NOT NULL,
            SkipPages INT NOT NULL,
            ChunksJson NVARCHAR(MAX) NULL,
            TotalChunks INT NOT NULL,
            CompletedChunks INT NOT NULL
                CONSTRAINT DF_ManualPublicationJobs_CompletedChunks DEFAULT (0),
            Status NVARCHAR(16) NOT NULL
                CONSTRAINT DF_ManualPublicationJobs_Status DEFAULT N'Queued',
            Error NVARCHAR(1000) NULL,
            BikeId INT NULL,
            CreatedAt DATETIME2 NOT NULL
                CONSTRAINT DF_ManualPublicationJobs_CreatedAt DEFAULT SYSUTCDATETIME(),
            UpdatedAt DATETIME2 NOT NULL
                CONSTRAINT DF_ManualPublicationJobs_UpdatedAt DEFAULT SYSUTCDATETIME(),
            CompletedAt DATETIME2 NULL,

            CONSTRAINT FK_ManualPublicationJobs_Users FOREIGN KEY (SubmittedByUserId)
                REFERENCES RideFix.Users (Id),
            CONSTRAINT FK_ManualPublicationJobs_MasterBikes FOREIGN KEY (BikeId)
                REFERENCES RideFix_Customs.MasterBikes (Id),
            CONSTRAINT CK_ManualPublicationJobs_Selection CHECK
            (
                LEN(LTRIM(RTRIM(Make))) > 0
                AND LEN(LTRIM(RTRIM(Model))) > 0
                AND LEN(LTRIM(RTRIM(ManualKey))) > 0
                AND LEN(LTRIM(RTRIM(CollectionName))) > 0
                AND Year BETWEEN 1900 AND 2100
                AND SkipPages BETWEEN 0 AND 999
            ),
            CONSTRAINT CK_ManualPublicationJobs_Progress CHECK
            (
                TotalChunks BETWEEN 1 AND 2000
                AND CompletedChunks BETWEEN 0 AND TotalChunks
            ),
            CONSTRAINT CK_ManualPublicationJobs_Text CHECK
            (
                ChunksJson IS NULL
                OR (ISJSON(ChunksJson) = 1 AND DATALENGTH(ChunksJson) <= 16777216)
            ),
            CONSTRAINT CK_ManualPublicationJobs_Lifecycle CHECK
            (
                (Status IN (N'Queued', N'Processing') AND CompletedAt IS NULL AND ChunksJson IS NOT NULL)
                OR
                (Status IN (N'Succeeded', N'Failed') AND CompletedAt IS NOT NULL AND ChunksJson IS NULL)
            ),
            CONSTRAINT CK_ManualPublicationJobs_Success CHECK
            (
                Status <> N'Succeeded'
                OR (CompletedChunks = TotalChunks AND BikeId IS NOT NULL AND Error IS NULL)
            )
        );
    END;

    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID('RideFix.ManualPublicationJobs')
            AND name = 'UX_ManualPublicationJobs_ActiveManual'
    )
        CREATE UNIQUE INDEX UX_ManualPublicationJobs_ActiveManual
            ON RideFix.ManualPublicationJobs (CollectionName, ManualKey)
            WHERE CompletedAt IS NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- Runtime MI needs SELECT, INSERT, UPDATE on this table; no permissions are changed by this script.
-- Existing Users/MasterBikes access and SQL application-lock permissions remain necessary.
-- Background processing requires the app process to be running. F1 idle/restarts can pause progress;
-- persisted jobs/checkpoints allow recovery, but this table is not an Always On/availability guarantee.
