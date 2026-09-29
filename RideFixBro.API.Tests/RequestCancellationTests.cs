using AutoGen.Core;
using Microsoft.Extensions.DependencyInjection;
using RideFixBro.API.Agents;
using RideFixBro.API.DataStore;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.API.Models;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

namespace RideFixBro.API.Tests
{
	public class RequestCancellationTests
	{
		[Fact]
		public async Task ChatCanCompleteAfterTheOld45SecondServerDeadline()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			var id = await factory.CreateChatAsync(client);
			factory.Agent.Reply = async cancellationToken =>
			{
				await Task.Delay(TimeSpan.FromSeconds(46), cancellationToken);
				return new TextMessage(Role.Assistant, "Slow answer completed");
			};
			var elapsed = Stopwatch.StartNew();
			using var response = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = id, message = "A longer request" }).WaitAsync(TimeSpan.FromSeconds(75));
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(45));
			Assert.Contains("Slow answer completed", await response.Content.ReadAsStringAsync());
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task CallerCancellationDoesNotSaveHistoryAndReleasesChatSlot(bool providerThrowsDifferentException)
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			var id = await factory.CreateChatAsync(client);
			using var warmup = await client.GetAsync("/api/me");
			Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
			var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			factory.Agent.Reply = async cancellationToken =>
			{
				try
				{
					started.TrySetResult();
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				}
				catch (OperationCanceledException) when (providerThrowsDifferentException)
				{
					throw new InvalidOperationException("Simulated provider-specific cancellation error.");
				}
				finally
				{
					stopped.TrySetResult();
				}
				return new TextMessage(Role.Assistant, "Should never be returned");
			};
			using var cancellation = new CancellationTokenSource();
			var request = client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = id, message = "Slow" }, cancellation.Token);
			try
			{
				await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
			}
			finally
			{
				cancellation.Cancel();
			}
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
			await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Empty(factory.History(id));

			factory.Agent.Reply = _ => Task.FromResult<IMessage>(new TextMessage(Role.Assistant, "Recovered"));
			using var next = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = id, message = "Retry" });
			Assert.Equal(HttpStatusCode.OK, next.StatusCode);
		}

		[Fact]
		public async Task CancellationReachesAToolAndStopsBeforeTheNextToolOrHistorySave()
		{
			using var handler = new RecordingHandler(ToolFlowTests.ToolReply(
				ToolFlowTests.Call("first", "SearchInternetAsync", """{"query":"slow"}""", "signature"),
				ToolFlowTests.Call("second", "SearchInternetAsync", """{"query":"never"}""")));
			var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var executions = 0;
			var agent = MechanicBroAgent.CreateCore(ToolFlowTests.CreateClient(handler),
				new[] { new FunctionContract
				{
					Name = "SearchInternetAsync",
					Parameters = new[] { new FunctionParameterContract
					{
						Name = "query", ParameterType = typeof(string), IsRequired = true
					}}
				} },
				new Dictionary<string, Func<string, string?, CancellationToken, Task<string>>>
				{
					["SearchInternetAsync"] = async (_, _, token) =>
					{
						executions++;
						started.TrySetResult();
						await Task.Delay(Timeout.InfiniteTimeSpan, token);
						return "Should never complete";
					}
				});
			using var cancel = new CancellationTokenSource();
			var store = new InMemoryChatStore();
			var request = ToolFlowTests.CreateManager(agent, store)
				.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Search", null), cancel.Token);
			await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
			cancel.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.Equal(1, executions);
			Assert.Empty(store.GetHistory(1));
			Assert.Single(handler.Requests);
		}
	}

}
