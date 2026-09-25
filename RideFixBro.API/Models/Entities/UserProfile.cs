namespace RideFixBro.API.Models.Entities
{
	public class UserProfile
	{
		public Guid Id { get; set; } = Guid.NewGuid();

		// Supabase token ka stable "sub" ID yahan aayega, actual JWT/password nahi.
		public Guid SupabaseUserId { get; set; }
		public string DisplayName { get; set; } = string.Empty;
		public string? Email { get; set; }

		// Role backend assign karega; client ki request se Admin nahi banayenge.
		public UserRole Role { get; set; } = UserRole.User;
		public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
		public List<Conversation> Conversations { get; set; } = new();
	}

	public enum UserRole
	{
		User,
		Admin
	}
}
