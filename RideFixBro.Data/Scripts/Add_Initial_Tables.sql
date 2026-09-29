CREATE SCHEMA RideFix;
GO
CREATE SCHEMA RideFix_Customs;
GO

-- 1. MasterRoles Table (Dictionary for roles)
CREATE TABLE RideFix_Customs.MasterUserRoles (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    RoleName NVARCHAR(100) NOT NULL UNIQUE,
    Description NVARCHAR(255) NOT NULL
);

-- Basic Roles Insert
INSERT INTO RideFix_Customs.MasterUserRoles (RoleName, Description) VALUES ('User', 'Standard app user');
INSERT INTO RideFix_Customs.MasterUserRoles (RoleName, Description) VALUES ('Admin', 'System administrator with full access');


-- 2. MasterBikes Table (Dictionary of supported bikes)
CREATE TABLE RideFix_Customs.MasterBikes (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Make NVARCHAR(100) NOT NULL, 
    Model NVARCHAR(100) NOT NULL, 
    Year INT NOT NULL,
    ManualKey NVARCHAR(128) NULL,

    CONSTRAINT CK_MasterBikes_ManualKey CHECK (ManualKey IS NULL OR LEN(LTRIM(RTRIM(ManualKey))) > 0)
);

-- Dummy master bike
INSERT INTO RideFix_Customs.MasterBikes (Make, Model, Year) VALUES ('Harley-Davidson', 'X440', 2024);


-- 3. Users Table
CREATE TABLE RideFix.Users (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    SupabaseUserId UNIQUEIDENTIFIER NOT NULL UNIQUE CHECK (SupabaseUserId <> '00000000-0000-0000-0000-000000000000'),
    Email NVARCHAR(255) NOT NULL,
    RoleId INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    
    CONSTRAINT FK_Users_MasterUserRoles FOREIGN KEY (RoleId) REFERENCES RideFix_Customs.MasterUserRoles(Id)
);


-- 4. UserBikes Table (User's Garage)
CREATE TABLE RideFix.UserBikes (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    UserId INT NOT NULL,
    BikeId INT NOT NULL,
    IsDeleted BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    
    CONSTRAINT FK_UserBikes_Users FOREIGN KEY (UserId) REFERENCES RideFix.Users(Id),
    CONSTRAINT FK_UserBikes_MasterBikes FOREIGN KEY (BikeId) REFERENCES RideFix_Customs.MasterBikes(Id),
    
    -- Ye unique constraint zaroori hai taaki ChatSessions ka composite FK kaam kare
    CONSTRAINT UQ_UserBikes_Id_UserId UNIQUE (Id, UserId)
);


-- 5. ChatSessions Table
CREATE TABLE RideFix.ChatSessions (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    UserId INT NOT NULL,
    SessionId NVARCHAR(128) NOT NULL CONSTRAINT DF_ChatSessions_SessionId DEFAULT (CONVERT(NVARCHAR(36), NEWID())),
    UserBikeId INT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    
    -- NULL bike means General. User must exist even when the composite bike FK has a NULL.
    CONSTRAINT FK_ChatSessions_Users FOREIGN KEY (UserId) REFERENCES RideFix.Users(Id),
    -- Ye cross-check karta hai ki jo bike chat mein hai, wo sach mein usi user ki hai
    CONSTRAINT FK_ChatSessions_UserBikeOwner FOREIGN KEY (UserBikeId, UserId) REFERENCES RideFix.UserBikes (Id, UserId)
);

CREATE UNIQUE INDEX UX_ChatSessions_UserId_SessionId ON RideFix.ChatSessions (UserId, SessionId);

-- 6. Messages Table (The Heavy Lifter)
CREATE TABLE RideFix.Messages (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    ChatSessionId INT NOT NULL,
    Role NVARCHAR(50) NOT NULL,
    Content NVARCHAR(MAX) NULL,
    PayloadJson NVARCHAR(MAX) NULL,
    TurnNumber INT NOT NULL,
    SequenceNumber INT NOT NULL,
    Timestamp DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    
    CONSTRAINT FK_Messages_ChatSessions FOREIGN KEY (ChatSessionId) REFERENCES RideFix.ChatSessions(Id) ON DELETE CASCADE,
    
    -- Constraints for AI logic
    CONSTRAINT CK_Messages_Role CHECK (Role IN ('User', 'Assistant', 'Tool', 'System')),
    CONSTRAINT CK_Messages_PayloadJson CHECK (PayloadJson IS NULL OR ISJSON(PayloadJson) = 1),
    CONSTRAINT CK_Messages_Content CHECK (Content IS NOT NULL OR PayloadJson IS NOT NULL),
    CONSTRAINT CK_Messages_Order CHECK (
        (TurnNumber IS NULL AND SequenceNumber IS NULL) OR 
        (TurnNumber IS NOT NULL AND SequenceNumber IS NOT NULL AND TurnNumber > 0 AND SequenceNumber > 0)
    ),
    
    -- Ek session ke andar same sequence number dobara nahi aa sakta
    CONSTRAINT UQ_Messages_Session_Sequence UNIQUE (ChatSessionId, SequenceNumber)
);
CREATE INDEX IX_Messages_Session_Turn ON RideFix.Messages (ChatSessionId, TurnNumber, SequenceNumber);
GO