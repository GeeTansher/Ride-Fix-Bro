using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RideFixBro.API.DataStore;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.API.Models.Entities;

namespace RideFixBro.API.Tests
{
	public class DatabaseModelTests
	{
		[Fact]
		public void ModelContainsOnlyTheThreeFoundationTables()
		{
			using var context = CreateContext();
			var tables = context.Model.GetEntityTypes().Select(entity => entity.GetTableName()).Order().ToArray();
			Assert.Equal(new[] { "Conversations", "Messages", "UserProfiles" }, tables);
			Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName);
		}

		[Fact]
		public void EachConversationAndMessageHasARequiredOwnerRelationship()
		{
			using var context = CreateContext();
			var conversation = context.Model.FindEntityType(typeof(Conversation))!;
			var owner = Assert.Single(conversation.GetForeignKeys());
			Assert.True(owner.IsRequired);
			Assert.Equal(typeof(UserProfile), owner.PrincipalEntityType.ClrType);
			Assert.Equal(nameof(Conversation.UserId), Assert.Single(owner.Properties).Name);
			Assert.Equal(DeleteBehavior.Cascade, owner.DeleteBehavior);

			var message = context.Model.FindEntityType(typeof(ChatMessage))!;
			var parent = Assert.Single(message.GetForeignKeys());
			Assert.True(parent.IsRequired);
			Assert.Equal(typeof(Conversation), parent.PrincipalEntityType.ClrType);
			Assert.Equal(nameof(ChatMessage.ConversationId), Assert.Single(parent.Properties).Name);
			Assert.Equal(DeleteBehavior.Cascade, parent.DeleteBehavior);
		}

		[Fact]
		public void ExternalUserIdentityAndMessageSequenceAreUnique()
		{
			using var context = CreateContext();
			var user = context.Model.FindEntityType(typeof(UserProfile))!;
			Assert.Contains(user.GetIndexes(), index => index.IsUnique &&
				index.Properties.Select(property => property.Name).SequenceEqual(new[] { nameof(UserProfile.SupabaseUserId) }));

			var message = context.Model.FindEntityType(typeof(ChatMessage))!;
			Assert.Contains(message.GetIndexes(), index => index.IsUnique &&
				index.Properties.Select(property => property.Name).SequenceEqual(
					new[] { nameof(ChatMessage.ConversationId), nameof(ChatMessage.SequenceNumber) }));
		}

		[Fact]
		public void RoleDefaultsAndConcurrencyMetadataAreConfigured()
		{
			using var context = CreateContext();
			Assert.Equal(UserRole.User, new UserProfile().Role);
			var userRole = context.Model.FindEntityType(typeof(UserProfile))!.FindProperty(nameof(UserProfile.Role))!;
			Assert.Equal(typeof(string), userRole.GetTypeMapping().Converter!.ProviderClrType);

			var version = context.Model.FindEntityType(typeof(Conversation))!.FindProperty(nameof(Conversation.RowVersion))!;
			Assert.True(version.IsConcurrencyToken);
			Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
			Assert.Equal("rowversion", version.GetColumnType());
		}

		[Fact]
		public void MessageModelAllowsToolMetadataAndRejectsInvalidOrderInTheSchema()
		{
			using var context = CreateContext();
			var message = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ChatMessage))!;
			Assert.True(message.FindProperty(nameof(ChatMessage.Content))!.IsNullable);
			Assert.True(message.FindProperty(nameof(ChatMessage.PayloadJson))!.IsNullable);
			var constraints = message.GetCheckConstraints().ToDictionary(
				constraint => Assert.IsType<string>(constraint.Name), constraint => constraint.Sql);
			Assert.Equal("[TurnNumber] > 0", constraints["CK_Messages_TurnNumber"]);
			Assert.Equal("[SequenceNumber] > 0", constraints["CK_Messages_SequenceNumber"]);
			Assert.Contains("'Tool'", constraints["CK_Messages_Role"]);
			Assert.Contains("ISJSON([PayloadJson])", constraints["CK_Messages_PayloadJson"]);
			Assert.Equal("[Content] IS NOT NULL OR [PayloadJson] IS NOT NULL", constraints["CK_Messages_Content"]);
		}

		[Fact]
		public void EfConnectsTheEntityRelationshipsWithoutOpeningADatabaseConnection()
		{
			using var context = CreateContext();
			var user = new UserProfile { SupabaseUserId = Guid.NewGuid(), DisplayName = "Test user" };
			var conversation = new Conversation();
			var message = new ChatMessage
			{
				TurnNumber = 1,
				SequenceNumber = 1,
				Role = MessageRole.User,
				Content = "Hello"
			};
			conversation.Messages.Add(message);
			user.Conversations.Add(conversation);

			context.UserProfiles.Add(user);

			Assert.Equal(user.Id, conversation.UserId);
			Assert.Equal(conversation.Id, message.ConversationId);
			Assert.Equal(3, context.ChangeTracker.Entries().Count());
			Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Added, entry.State));
		}

		[Fact]
		public void InitialMigrationMatchesTheModelAndProducesSqlOffline()
		{
			using var context = CreateContext();
			Assert.EndsWith("_InitialPersistence", Assert.Single(context.Database.GetMigrations()));
			Assert.False(context.Database.HasPendingModelChanges());

			var script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
			Assert.Contains("CREATE TABLE [UserProfiles]", script);
			Assert.Contains("CREATE TABLE [Conversations]", script);
			Assert.Contains("CREATE TABLE [Messages]", script);
			Assert.Contains("UNIQUE INDEX [IX_UserProfiles_SupabaseUserId]", script);
			Assert.Contains("UNIQUE INDEX [IX_Messages_ConversationId_SequenceNumber]", script);
			Assert.Contains("ON DELETE CASCADE", script);
			Assert.Contains("[RowVersion] rowversion", script);
		}

		[Fact]
		public async Task MissingSqlConfigurationIsExplicitAndDoesNotReplaceTheRamStore()
		{
			await using var app = new ChatApiFactory();
			await using var factory = app.WithWebHostBuilder(builder =>
			{
				builder.ConfigureAppConfiguration((_, config) =>
					config.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:RideFixBro"] = ""
					}));
			});
			using var scope = factory.Services.CreateScope();

			var error = Assert.Throws<InvalidOperationException>(
				() => scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>());
			Assert.Contains("ConnectionStrings:RideFixBro", error.Message);
			Assert.IsType<InMemoryChatStore>(scope.ServiceProvider.GetRequiredService<IChatHistoryStore>());
		}

		private static RideFixBroDbContext CreateContext()
		{
			// Model/migration checks only: no connection string and no live database.
			var options = new DbContextOptionsBuilder<RideFixBroDbContext>().UseSqlServer().Options;
			return new RideFixBroDbContext(options);
		}
	}
}
