using AutoGen.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RideFixBro.API.DataStore;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.API.Models;
using RideFixBro.API.Services;
using RideFixBro.Data.Entities;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RideFixBro.API.Tests
{
	public class WorkspaceApiTests
	{
		[Fact]
		public async Task ProfileUsesNameForDisplayButNeverForRoleAuthorization()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			client.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(overrides: new()
			{
				["user_metadata"] = new Dictionary<string, object> { ["full_name"] = "Demo Rider", ["role"] = "Admin" }
			}));
			var profile = await client.GetFromJsonAsync<JsonElement>("/api/me");
			Assert.Equal("Demo Rider", profile.GetProperty("name").GetString());
			Assert.Equal("User", profile.GetProperty("role").GetString());
		}

		[Fact]
		public async Task RemoveHidesTheBikePreservesLinkedRecordsAndReAddRestoresTheSameEntry()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			await SeedCatalog(factory);
			var bike = await AddBike(client, 101);
			using (var scope = factory.Services.CreateScope())
			{
				var database = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
				var user = await database.Users.SingleAsync();
				var chat = new ChatSession { UserId = user.Id, UserBikeId = bike.Id, CreatedAt = DateTime.UtcNow, IsActive = true };
				database.ChatSessions.Add(chat);
				await database.SaveChangesAsync();
				database.Messages.Add(new RideFixBro.Data.Entities.Message
				{
					ChatSessionId = chat.Id, Role = "User", Content = "Saved text",
					TurnNumber = 1, SequenceNumber = 1, Timestamp = DateTime.UtcNow
				});
				await database.SaveChangesAsync();
			}

			using var deleted = await client.DeleteAsync($"/api/garage/{bike.Id}");
			Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
			Assert.Empty((await client.GetFromJsonAsync<List<GarageBikeResponse>>("/api/garage"))!);
			using var repeatedDelete = await client.DeleteAsync($"/api/garage/{bike.Id}");
			Assert.Equal(HttpStatusCode.NoContent, repeatedDelete.StatusCode);
			using (var scope = factory.Services.CreateScope())
			{
				var database = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
				Assert.Equal(1, await database.UserBikes.CountAsync());
				Assert.Equal(1, await database.ChatSessions.CountAsync());
				Assert.Equal(1, await database.Messages.CountAsync());
				Assert.True(await database.UserBikes.Select(row => row.IsDeleted).SingleAsync());
			}
			var restored = await AddBike(client, 101);
			Assert.Equal(bike.Id, restored.Id);
			Assert.Single((await client.GetFromJsonAsync<List<GarageBikeResponse>>("/api/garage"))!);
		}

		[Fact]
		public async Task UsersCannotDeleteOrChatWithAnotherUsersBike()
		{
			await using var factory = new ChatApiFactory();
			using var owner = factory.Client();
			using var other = factory.Client();
			other.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(Guid.NewGuid()));
			await SeedCatalog(factory);
			var bike = await AddBike(owner, 101);
			using var deleted = await other.DeleteAsync($"/api/garage/{bike.Id}");
			Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
			using var chat = await other.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "other", userBikeId = bike.Id, message = "Hi" });
			Assert.Equal(HttpStatusCode.NotFound, chat.StatusCode);
			Assert.Equal(0, factory.Agent.Calls);
			Assert.Single((await owner.GetFromJsonAsync<List<GarageBikeResponse>>("/api/garage"))!);
		}

		[Fact]
		public async Task DeleteRequiresAuthentication()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client(false);
			using var deleted = await client.DeleteAsync("/api/garage/1");
			Assert.Equal(HttpStatusCode.Unauthorized, deleted.StatusCode);
		}

		[Fact]
		public async Task FirstMessageLocksBikeOnTheServerAndOldChatCanUseARemovedBike()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			await SeedCatalog(factory);
			var firstBike = await AddBike(client, 101);
			var secondBike = await AddBike(client, 102);
			using var first = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "locked", userBikeId = firstBike.Id, message = "First" });
			Assert.Equal(HttpStatusCode.OK, first.StatusCode);
			using var changed = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "locked", userBikeId = secondBike.Id, message = "Changed" });
			Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
			using var generalSwitch = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "locked", isGeneral = true, message = "Change to General" });
			Assert.Equal(HttpStatusCode.Conflict, generalSwitch.StatusCode);
			Assert.Equal(1, factory.Agent.Calls);

			using var removed = await client.DeleteAsync($"/api/garage/{firstBike.Id}");
			Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
			using var followUp = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "locked", message = "Follow-up without repeated bike ID" });
			Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
			using var newChat = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "new", userBikeId = firstBike.Id, message = "New" });
			Assert.Equal(HttpStatusCode.NotFound, newChat.StatusCode);
			var history = factory.Services.GetRequiredService<IChatHistoryStore>();
			Assert.Equal(firstBike.Id, history.GetSelectedBikeId($"{AuthTestTokens.UserId:D}:locked"));
			var message = Assert.IsType<TextMessage>(history.GetHistory($"{AuthTestTokens.UserId:D}:locked")[0]);
			Assert.Contains("Harley-Davidson X440, year 2024", message.Content);
		}

		[Fact]
		public async Task InFlightTurnCannotBeReboundToAnotherBike()
		{
			var store = new InMemoryChatStore();
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var first = store.UpdateHistoryAsync("user:chat", async history =>
			{
				entered.SetResult();
				await release.Task;
				return "done";
			}, userBikeId: 1);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var second = store.UpdateHistoryAsync("user:chat", history => Task.FromResult("wrong bike"), userBikeId: 2);
			release.SetResult();
			await first;
			var error = await Assert.ThrowsAsync<ChatInputException>(() => second);
			Assert.Equal(409, error.StatusCode);
			Assert.Equal(1, store.GetSelectedBikeId("user:chat"));
		}

		[Fact]
		public async Task AnotherModelDoesNotAdvertiseOrExecuteTheX440ManualTool()
		{
			using var handler = new RecordingHandler(ToolFlowTests.ToolReply(
				ToolFlowTests.Call("manual", "SearchManualAsync", """{"userQuery":"specs"}""", "signature")));
			var calls = 0;
			var agent = ToolFlowTests.CreateAgent(handler, manual: _ =>
			{
				calls++;
				return Task.FromResult("Wrong bike manual");
			});
			var manager = ToolFlowTests.CreateManager(agent, new InMemoryChatStore());
			await Assert.ThrowsAsync<ChatLimitExceededException>(() => manager.AskMechanicBro(
				AuthTestTokens.UserId, "new-bike", "Specs", selectedBike:
					new GarageBikeResponse(2, 102, "Another make", "Another model", 2025, DateTime.UtcNow)));
			Assert.Equal(0, calls);
			var tools = handler.Requests[0].GetProperty("tools").EnumerateArray()
				.Select(tool => tool.GetProperty("function").GetProperty("name").GetString());
			Assert.DoesNotContain("SearchManualAsync", tools);
		}

		[Fact]
		public async Task GeneralSelectionNeedsNoGarageAndIsInheritedByFollowUps()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();

			using var general = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "general", isGeneral = true, message = "Hi" });
			Assert.Equal(HttpStatusCode.OK, general.StatusCode);
			using var changed = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "general", userBikeId = 1, message = "Change to bike" });
			Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

			using var followUp = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "general", message = "Continue General" });
			Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
			var history = factory.Services.GetRequiredService<IChatHistoryStore>();
			Assert.True(history.IsGeneralSession($"{AuthTestTokens.UserId:D}:general"));
			Assert.Null(history.GetSelectedBikeId($"{AuthTestTokens.UserId:D}:general"));
			var latestUser = Assert.IsType<TextMessage>(history.GetHistory($"{AuthTestTokens.UserId:D}:general")[2]);
			Assert.Contains("General chat: no motorcycle is selected", latestUser.Content);
			Assert.Equal(2, factory.Agent.Calls);
		}

		[Fact]
		public async Task FailedGeneralAnswerStillLocksSelectionAndAllowsAGeneralRetry()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			factory.Agent.Reply = _ => throw new InvalidOperationException("Provider unavailable");
			using var failed = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "failed-general", isGeneral = true, message = "Hi" });
			Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
			var history = factory.Services.GetRequiredService<IChatHistoryStore>();
			var key = $"{AuthTestTokens.UserId:D}:failed-general";
			Assert.Empty(history.GetHistory(key));
			Assert.True(history.IsGeneralSession(key));

			using var changed = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "failed-general", userBikeId = 1, message = "Change to bike" });
			Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
			factory.Agent.Reply = _ => Task.FromResult<IMessage>(new TextMessage(Role.Assistant, "Answer", "MechanicBro"));
			using var retry = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "failed-general", message = "Try again" });
			Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
			Assert.Equal(2, history.GetHistory(key).Count);
			Assert.True(history.IsGeneralSession(key));
			Assert.Equal(2, factory.Agent.Calls);
		}

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public async Task InFlightTurnCannotSwitchBetweenGeneralAndBike(bool firstIsGeneral)
		{
			var store = new InMemoryChatStore();
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var first = store.UpdateHistoryAsync("user:chat", async history =>
			{
				entered.SetResult();
				await release.Task;
				return "done";
			}, userBikeId: firstIsGeneral ? null : 1, isGeneral: firstIsGeneral);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var second = store.UpdateHistoryAsync("user:chat", history => Task.FromResult("wrong selection"),
				userBikeId: firstIsGeneral ? 1 : null, isGeneral: !firstIsGeneral);
			release.SetResult();
			await first;
			var error = await Assert.ThrowsAsync<ChatInputException>(() => second);
			Assert.Equal(409, error.StatusCode);
			Assert.Equal(firstIsGeneral, store.IsGeneralSession("user:chat"));
			Assert.Equal(firstIsGeneral ? (int?)null : 1, store.GetSelectedBikeId("user:chat"));
		}

		[Fact]
		public async Task GeneralAndBikeCannotBeRequestedTogether()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			using var response = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "invalid", isGeneral = true, userBikeId = 1, message = "Hi" });
			Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			Assert.Equal(0, factory.Agent.Calls);
		}

		[Fact]
		public async Task GeneralChatUsesInternetButDoesNotAdvertiseBikeManualSearch()
		{
			using var handler = new RecordingHandler(
				ToolFlowTests.ToolReply(ToolFlowTests.Call("web", "SearchInternetAsync", """{"query":"general riding advice"}""", "signature")),
				ToolFlowTests.TextReply("General answer"));
			var calls = 0;
			var agent = ToolFlowTests.CreateAgent(handler, internet: _ =>
			{
				calls++;
				return Task.FromResult("Internet result");
			});
			var store = new InMemoryChatStore();
			var reply = await ToolFlowTests.CreateManager(agent, store).AskMechanicBro(
				AuthTestTokens.UserId, "general-web", "General question", isGeneral: true);
			Assert.Equal("General answer", reply);
			Assert.Equal(1, calls);
			foreach (var request in handler.Requests)
			{
				var tools = request.GetProperty("tools").EnumerateArray()
					.Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
				Assert.Equal(new[] { "SearchInternetAsync" }, tools);
			}
			Assert.True(store.IsGeneralSession($"{AuthTestTokens.UserId:D}:general-web"));
		}

		[Fact]
		public async Task GeneralChatCannotExecuteAManualEvenIfTheModelRequestsOne()
		{
			using var handler = new RecordingHandler(ToolFlowTests.ToolReply(
				ToolFlowTests.Call("manual", "SearchManualAsync", """{"userQuery":"specs"}""", "signature")));
			var calls = 0;
			var agent = ToolFlowTests.CreateAgent(handler, manual: _ =>
			{
				calls++;
				return Task.FromResult("Should not run");
			});
			var store = new InMemoryChatStore();
			await Assert.ThrowsAsync<ChatLimitExceededException>(() => ToolFlowTests.CreateManager(agent, store)
				.AskMechanicBro(AuthTestTokens.UserId, "general-manual", "Hi", isGeneral: true));
			Assert.Equal(0, calls);
			Assert.Empty(store.GetHistory($"{AuthTestTokens.UserId:D}:general-manual"));
			Assert.True(store.IsGeneralSession($"{AuthTestTokens.UserId:D}:general-manual"));
		}

		private static async Task<GarageBikeResponse> AddBike(HttpClient client, int bikeId)
		{
			using var response = await client.PostAsJsonAsync("/api/garage", new { bikeId });
			Assert.True(response.IsSuccessStatusCode);
			return (await response.Content.ReadFromJsonAsync<GarageBikeResponse>())!;
		}

		private static async Task SeedCatalog(ChatApiFactory factory)
		{
			using var scope = factory.Services.CreateScope();
			var database = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
			database.MasterBikes.AddRange(
				new MasterBike { Id = 101, Make = "Harley-Davidson", Model = "X440", Year = 2024 },
				new MasterBike { Id = 102, Make = "Harley-Davidson", Model = "X440", Year = 2025 });
			await database.SaveChangesAsync();
		}
	}
}
