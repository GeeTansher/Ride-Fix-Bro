using Microsoft.EntityFrameworkCore;
using RideFixBro.API.Models.Entities;

namespace RideFixBro.API.DataStore
{
	public class RideFixBroDbContext : DbContext
	{
		public RideFixBroDbContext(DbContextOptions<RideFixBroDbContext> options) : base(options)
		{
		}

		public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
		public DbSet<Conversation> Conversations => Set<Conversation>();
		public DbSet<ChatMessage> Messages => Set<ChatMessage>();

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			base.OnModelCreating(modelBuilder);

			var users = modelBuilder.Entity<UserProfile>();
			users.ToTable("UserProfiles", table =>
			{
				table.HasCheckConstraint("CK_UserProfiles_Role", "[Role] IN ('User', 'Admin')");
				table.HasCheckConstraint("CK_UserProfiles_SupabaseUserId",
					"[SupabaseUserId] <> '00000000-0000-0000-0000-000000000000'");
			});
			users.HasKey(user => user.Id);
			users.HasIndex(user => user.SupabaseUserId).IsUnique();
			users.Property(user => user.DisplayName).HasMaxLength(100).IsRequired();
			users.Property(user => user.Email).HasMaxLength(320);
			users.Property(user => user.Role).HasConversion<string>().HasMaxLength(20)
				.HasDefaultValue(UserRole.User);

			var conversations = modelBuilder.Entity<Conversation>();
			conversations.ToTable("Conversations");
			conversations.HasKey(conversation => conversation.Id);
			conversations.Property(conversation => conversation.Title).HasMaxLength(200).IsRequired();
			conversations.Property(conversation => conversation.RowVersion).IsRowVersion();
			conversations.HasIndex(conversation => new { conversation.UserId, conversation.UpdatedAt });
			conversations.HasOne(conversation => conversation.User)
				.WithMany(user => user.Conversations)
				.HasForeignKey(conversation => conversation.UserId)
				.OnDelete(DeleteBehavior.Cascade);

			var messages = modelBuilder.Entity<ChatMessage>();
			messages.ToTable("Messages", table =>
			{
				table.HasCheckConstraint("CK_Messages_TurnNumber", "[TurnNumber] > 0");
				table.HasCheckConstraint("CK_Messages_SequenceNumber", "[SequenceNumber] > 0");
				table.HasCheckConstraint("CK_Messages_Role", "[Role] IN ('User', 'Assistant', 'Tool', 'System')");
				table.HasCheckConstraint("CK_Messages_PayloadJson", "[PayloadJson] IS NULL OR ISJSON([PayloadJson]) = 1");
				table.HasCheckConstraint("CK_Messages_Content", "[Content] IS NOT NULL OR [PayloadJson] IS NOT NULL");
			});
			messages.HasKey(message => message.Id);
			messages.Property(message => message.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
			messages.HasIndex(message => new { message.ConversationId, message.SequenceNumber }).IsUnique();
			messages.HasOne(message => message.Conversation)
				.WithMany(conversation => conversation.Messages)
				.HasForeignKey(message => message.ConversationId)
				.OnDelete(DeleteBehavior.Cascade);
		}
	}
}
