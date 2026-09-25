namespace RideFixBro.API.Models.Entities
{
	public class ChatMessage
	{
		public Guid Id { get; set; } = Guid.NewGuid();
		public Guid ConversationId { get; set; }
		public Conversation? Conversation { get; set; }

		// Dono 1 se start honge: turn se poora exchange milega, sequence se message order.
		public int TurnNumber { get; set; }
		public int SequenceNumber { get; set; }
		public MessageRole Role { get; set; }
		public string? Content { get; set; }

		// Tool IDs, arguments/results aur Gemini metadata ka JSON; serializer next step mein aayega.
		public string? PayloadJson { get; set; }
		public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
	}

	public enum MessageRole
	{
		User,
		Assistant,
		Tool,
		System
	}
}
