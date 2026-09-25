namespace RideFixBro.API.Models.Entities
{
	public class Conversation
	{
		public Guid Id { get; set; } = Guid.NewGuid();
		public Guid UserId { get; set; }
		public UserProfile? User { get; set; }
		public string Title { get; set; } = "New chat";
		public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
		public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

		// Do writers same conversation save karein toh SQL update conflict detect kar sake.
		public byte[] RowVersion { get; set; } = Array.Empty<byte>();
		public List<ChatMessage> Messages { get; set; } = new();
	}
}
