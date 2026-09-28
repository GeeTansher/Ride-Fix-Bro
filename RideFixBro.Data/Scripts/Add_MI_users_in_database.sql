-- Dhyan rakh: Brackets ke andar apni MI ka naam (e.g., RideFixBro-MI) daalna
CREATE USER [mi-ridefixbro-dev] FROM EXTERNAL PROVIDER;

ALTER ROLE db_datareader ADD MEMBER [mi-ridefixbro-dev];
ALTER ROLE db_datawriter ADD MEMBER [mi-ridefixbro-dev];