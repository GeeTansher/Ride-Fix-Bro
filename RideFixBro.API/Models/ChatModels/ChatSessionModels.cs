using RideFixBro.API.Models.GarageModels;

namespace RideFixBro.API.Models.ChatModels;

public record ChatSummary(int Id, string Title, GarageBikeResponse? Bike, bool IsGeneral, DateTime UpdatedAt);
public record SavedChatMessage(int SequenceNumber, string Text, bool IsUser, bool PhotoNotStored);
public record ChatDetail(ChatSummary Chat, List<SavedChatMessage> Messages, int? NextBeforeSequence);
public record ChatReply(int SessionId, string Reply);
