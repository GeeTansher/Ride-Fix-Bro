namespace RideFixBro.API.Models;

// Backend context only: API responses use GarageBikeResponse/ChatSummary instead.
public sealed record BikeContext(int UserBikeId, string Make, string Model, int Year, string? ManualKey);
public sealed record ChatContext(int Id, BikeContext? Bike, int UserId)
{
    public bool IsGeneral => Bike is null;
}

public sealed record ChatInput(string Message, string? ImageDataUri);
