namespace RideFixBro.API.Models;

public record ChatSummary(int Id, string Title, GarageBikeResponse? Bike, bool IsGeneral, DateTime UpdatedAt);
public record SavedChatMessage(int SequenceNumber, string Text, bool IsUser, bool PhotoNotStored);
public record ChatDetail(ChatSummary Chat, List<SavedChatMessage> Messages, int? NextBeforeSequence);
