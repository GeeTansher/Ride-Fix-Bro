-- Pehle schema create karna padta hai
CREATE SCHEMA RideFix;
GO
CREATE SCHEMA RideFix_Customs;
GO

-- 1. Users Table
CREATE TABLE RideFix.Users (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Email NVARCHAR(255) NOT NULL,
    Role NVARCHAR(50) NOT NULL DEFAULT 'User',
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

-- 2. MasterBikes Table (The dictionary of supported bikes)
CREATE TABLE RideFix_Customs.MasterBikes (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Make NVARCHAR(100) NOT NULL, -- e.g., Harley-Davidson, Royal Enfield
    Model NVARCHAR(100) NOT NULL, -- e.g., X440, Classic 350
    Year INT NOT NULL
);

-- 3. UserBikes Table (User's Garage - linking User to MasterBike)
CREATE TABLE RideFix.UserBikes (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    UserId INT NOT NULL,
    MasterBikeId INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_UserBikes_Users FOREIGN KEY (UserId) REFERENCES RideFix.Users(Id),
    CONSTRAINT FK_UserBikes_MasterBikes FOREIGN KEY (MasterBikeId) REFERENCES RideFix_Customs.MasterBikes(Id)
);

-- 4. ChatSessions Table (Locked to a specific bike from User's Garage)
CREATE TABLE RideFix.ChatSessions (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    UserId INT NOT NULL,
    UserBikeId INT NOT NULL, -- Locks the chat to a specific bike they own
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_ChatSessions_Users FOREIGN KEY (UserId) REFERENCES RideFix.Users(Id),
    -- Dhyan rakh: No cascade delete here to prevent accidental chat wipeouts if a bike is removed
    CONSTRAINT FK_ChatSessions_UserBikes FOREIGN KEY (UserBikeId) REFERENCES RideFix.UserBikes(Id) 
);

-- 5. Messages Table
CREATE TABLE RideFix.Messages (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    ChatSessionId INT NOT NULL,
    Role NVARCHAR(50) NOT NULL,
    Content NVARCHAR(MAX) NOT NULL,
    Timestamp DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_Messages_ChatSessions FOREIGN KEY (ChatSessionId) REFERENCES RideFix.ChatSessions(Id) ON DELETE CASCADE
);

-- Ek dummy master bike insert kar dete hain teri aasaani ke liye!
INSERT INTO RideFix_Customs.MasterBikes (Make, Model, Year) VALUES ('Harley-Davidson', 'X440', 2024);