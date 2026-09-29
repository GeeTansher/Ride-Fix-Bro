using AutoGen.Core;
using RideFixBro.API.DataStore;
using RideFixBro.API.Services;
using RideFixBro.API.Models;
using static RideFixBro.API.Tests.ToolFlowTests;

namespace RideFixBro.API.Tests
{
	public class ChatBudgetAndHistoryTests
	{
		[Fact]
		public async Task AllowsSixToolsThenAFinalAnswerAndResetsForTheNextTurn()
		{
			using var handler = new RecordingHandler(Batch("first", 6), TextReply("First"),
				Batch("second", 6), TextReply("Second"));
			var executions = 0;
			var agent = CreateAgent(handler, _ =>
			{
				executions++;
				return Task.FromResult("result");
			});
			var manager = CreateManager(agent, new InMemoryChatStore());

			Assert.Equal(new ChatReply(1, "First"), await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("First", null)));
			Assert.Equal(new ChatReply(1, "Second"), await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Second", null)));
			Assert.Equal(12, executions);
			Assert.All(handler.Requests, AssertValidSequence);
		}

		[Fact]
		public async Task SevenToolBatchIsRejectedBeforeAnyToolExecutes()
		{
			using var handler = new RecordingHandler(Batch("too-many", 7));
			var executions = 0;
			var agent = CreateAgent(handler, _ =>
			{
				executions++;
				return Task.FromResult("result");
			});
			var store = new InMemoryChatStore();

			await Assert.ThrowsAsync<ChatLimitExceededException>(
				() => CreateManager(agent, store).AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Search", null)));
			Assert.Equal(0, executions);
			Assert.Single(handler.Requests);
			Assert.Empty(store.GetHistory(1));
		}

		[Fact]
		public async Task ToolBudgetCountsAcrossRoundsAndRollsBackOnlyTheFailedTurn()
		{
			using var handler = new RecordingHandler(TextReply("Saved"), Batch("first", 4), Batch("second", 3));
			var executions = 0;
			var agent = CreateAgent(handler, _ =>
			{
				executions++;
				return Task.FromResult("result");
			});
			var store = new InMemoryChatStore();
			var manager = CreateManager(agent, store);
			await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Saved question", null));

			await Assert.ThrowsAsync<ChatLimitExceededException>(() => manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Too much", null)));
			Assert.Equal(4, executions);
			Assert.Equal(2, store.GetHistory(1).Count);
			Assert.StartsWith("Saved question", Assert.IsAssignableFrom<TextMessage>(store.GetHistory(1)[0]).Content);
		}

		[Fact]
		public async Task ToolBudgetCanBeConfiguredBelowTheRoundLimit()
		{
			using var handler = new RecordingHandler(Batch("too-many", 3));
			var manager = CreateManager(CreateAgent(handler), new InMemoryChatStore(), maxToolCalls: 2);
			await Assert.ThrowsAsync<ChatLimitExceededException>(() => manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Search", null)));
		}

		[Fact]
		public async Task SendsTenWholeTurnsIncludingSignaturesWithoutDeletingStoredTurns()
		{
			var responses = Enumerable.Range(1, 12).SelectMany(i =>
				new[] { Batch($"turn-{i}", 1), TextReply($"Answer {i}") }).ToArray();
			using var handler = new RecordingHandler(responses);
			var store = new InMemoryChatStore();
			var manager = CreateManager(CreateAgent(handler), store);
			var image = $"data:image/png;base64,{Convert.ToBase64String(ImageFixtures.Png())}";

			for (var i = 1; i <= 12; i++)
			{
				await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput($"Question {i}", i == 1 ? image : null));
			}

			var history = store.GetHistory(1);
			Assert.Equal(36, history.Count);
			Assert.IsType<MultiModalMessage>(history[0]);
			Assert.Equal("Answer 12", Assert.IsType<TextMessage>(history[^1]).Content);
			var messages = handler.Requests[^1].GetProperty("messages");
			Assert.StartsWith("Question 3", messages[1].GetProperty("content").GetString());
			Assert.Equal("signature-turn-3", messages[2].GetProperty("tool_calls")[0]
				.GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString());
			Assert.All(handler.Requests, AssertValidSequence);
		}

		[Fact]
		public async Task FailedTurnDoesNotCommitHistoryTrimming()
		{
			var responses = Enumerable.Range(1, 10).Select(i => TextReply($"Answer {i}"))
				.Append(Batch("too-many", 7)).Append(TextReply("Recovered")).ToArray();
			using var handler = new RecordingHandler(responses);
			var store = new InMemoryChatStore();
			var manager = CreateManager(CreateAgent(handler), store);
			for (var i = 1; i <= 10; i++)
			{
				await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput($"Question {i}", null));
			}

			var saved = store.GetHistory(1);
			await Assert.ThrowsAsync<ChatLimitExceededException>(() => manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Failed", null)));
			Assert.Equal(saved, store.GetHistory(1));
			Assert.Equal(new ChatReply(1, "Recovered"), await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Retry", null)));
			Assert.StartsWith("Question 1", Assert.IsAssignableFrom<TextMessage>(store.GetHistory(1)[0]).Content);
			Assert.Equal(22, store.GetHistory(1).Count);
			Assert.StartsWith("Question 2", handler.Requests[^1].GetProperty("messages")[1].GetProperty("content").GetString());
		}

		[Fact]
		public async Task AOneTurnHistoryDoesNotKeepAnyPreviousConversationMessages()
		{
			using var handler = new RecordingHandler(TextReply("First"), TextReply("Second"));
			var store = new InMemoryChatStore();
			var manager = CreateManager(CreateAgent(handler), store, maxHistoryTurns: 1);
			await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("First", null));
			await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Second", null));
			Assert.Equal(4, store.GetHistory(1).Count);
			Assert.Equal(2, handler.Requests[1].GetProperty("messages").GetArrayLength());
		}

		private static string Batch(string prefix, int count) => ToolReply(Enumerable.Range(1, count)
			.Select(i => Call($"{prefix}-{i}", "SearchInternetAsync", """{"query":"search"}""",
				i == 1 ? $"signature-{prefix}" : null)).ToArray());
	}
}
