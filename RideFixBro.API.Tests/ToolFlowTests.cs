using AutoGen.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using OpenAI.Chat;
using RideFixBro.API.Agents;
using RideFixBro.API.Configuration;
using RideFixBro.API.DataStore;
using RideFixBro.API.Services;
using RideFixBro.API.Models;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;

namespace RideFixBro.API.Tests
{
	public class ToolFlowTests
	{
		[Fact]
		public async Task ParallelSequentialAndFollowUpToolsPreserveWireProtocol()
		{
			using var handler = new RecordingHandler(
				ToolReply(Call("web-1", "SearchInternetAsync", """{"query":"jacket prices"}""", "signature-A"),
					Call("manual-1", "SearchManualAsync", """{"userQuery":"repair instructions"}""")),
				ToolReply(Call("manual-2", "SearchManualAsync", """{"userQuery":"more details"}""", "signature-B")),
				TextReply("Bhai, here is the answer."),
				ToolReply(Call("web-2", "SearchInternetAsync", """{"query":"follow up"}""", "signature-C")),
				TextReply("Bhai, follow-up answer."));
			var invoked = new List<string>();
			var agent = CreateAgent(handler, arguments =>
			{
				invoked.Add(arguments);
				return Task.FromResult("Live search: \"quoted\" result\nsecond line");
			}, arguments =>
			{
				invoked.Add(arguments);
				return Task.FromResult("Manual passage");
			});
			var store = new InMemoryChatStore();
			var manager = CreateManager(agent, store);

			Assert.Equal(new ChatReply(1, "Bhai, here is the answer."), await manager.AskMechanicBro(new ChatContext(1, ManualBike, 1), new ChatInput("First question", null)));
			Assert.Equal(new ChatReply(1, "Bhai, follow-up answer."), await manager.AskMechanicBro(new ChatContext(1, ManualBike, 1), new ChatInput("Next question", null)));

			Assert.Equal(4, invoked.Count);
			Assert.Equal(7, store.GetHistory(1).Count);
			foreach (var exchange in store.GetHistory(1)
				.OfType<AggregateMessage<ToolCallMessage, ToolCallResultMessage>>())
			{
				Assert.All(exchange.Message1.ToolCalls, call => Assert.Null(call.Result));
			}

			var secondRequest = handler.Requests[1];
			Assert.Equal(new[] { "system", "user", "assistant", "tool", "tool" }, Roles(secondRequest));
			var messages = secondRequest.GetProperty("messages");
			Assert.Equal("", messages[2].GetProperty("content").GetString());
			Assert.Equal("signature-A", Signature(messages[2], 0));
			Assert.False(messages[2].GetProperty("tool_calls")[1].TryGetProperty("extra_content", out _));
			Assert.Equal("web-1", messages[3].GetProperty("tool_call_id").GetString());
			Assert.Equal("manual-1", messages[4].GetProperty("tool_call_id").GetString());
			using var webResult = JsonDocument.Parse(messages[3].GetProperty("content").GetString()!);
			Assert.Equal("Live search: \"quoted\" result\nsecond line", webResult.RootElement.GetProperty("result").GetString());

			var thirdMessages = handler.Requests[2].GetProperty("messages");
			Assert.Equal("signature-A", Signature(thirdMessages[2], 0));
			Assert.Equal("signature-B", Signature(thirdMessages[5], 0));
			Assert.Equal("manual-2", thirdMessages[6].GetProperty("tool_call_id").GetString());
			Assert.Equal("assistant", handler.Requests[3].GetProperty("messages")[7].GetProperty("role").GetString());
			var lastMessages = handler.Requests[4].GetProperty("messages");
			Assert.Equal("signature-A", Signature(lastMessages[2], 0));
			Assert.Equal("signature-B", Signature(lastMessages[5], 0));
			Assert.Equal("signature-C", Signature(lastMessages[9], 0));
			Assert.All(handler.Requests, request => AssertValidSequence(request));
		}

		[Fact]
		public async Task ProductionFactoryUsesGeneratedInternetContractAndCustomExecution()
		{
			using var modelHandler = new RecordingHandler(
				ToolReply(Call("web", "SearchInternetAsync", """{"query":"helmet price"}""", "signature")),
				TextReply("Found the price."));
			using var searchHandler = new RecordingHandler("""
				{"answer":"Verified search result","results":[{"title":"Source report","url":"https://sources.example.test/report","content":"Retrieved excerpt"}]}
				""");
			using var searchClient = new HttpClient(searchHandler);
			var config = Configuration();
			var tavily = new TavilySearchService(config, searchClient);
			var vector = new VectorDbService(config);
			var agent = MechanicBroAgent.Create(CreateClient(modelHandler), tavily, vector);
			var manager = CreateManager(agent, new InMemoryChatStore());

			Assert.Equal(new ChatReply(1, "Found the price."), await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Search", null)));
			Assert.Equal("helmet price", Assert.Single(searchHandler.Requests).GetProperty("query").GetString());
			var tools = modelHandler.Requests[0].GetProperty("tools").EnumerateArray()
				.Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray();
			Assert.Equal(new[] { "SearchInternetAsync" }, tools);
			Assert.Contains("Verified search result", modelHandler.Requests[1].GetRawText());
			var toolResult = modelHandler.Requests[1].GetProperty("messages").EnumerateArray()
				.Single(message => message.GetProperty("role").GetString() == "tool");
			using var wrapped = JsonDocument.Parse(toolResult.GetProperty("content").GetString()!);
			using var evidence = JsonDocument.Parse(wrapped.RootElement.GetProperty("result").GetString()!);
			Assert.Equal("https://sources.example.test/report", evidence.RootElement.GetProperty("results")[0].GetProperty("url").GetString());
			Assert.Equal("Retrieved excerpt", evidence.RootElement.GetProperty("results")[0].GetProperty("content").GetString());
		}

		[Fact]
		public async Task ManualCanBeFollowedByWebResearchWithClearSourceInstructions()
		{
			using var handler = new RecordingHandler(
				ToolReply(Call("manual", "SearchManualAsync", """{"userQuery":"maintenance"}""", "manual-signature")),
				ToolReply(Call("web", "SearchInternetAsync", """{"query":"X440 2024 owner maintenance experiences Reddit"}""", "web-signature")),
				TextReply("Manual se: manual findings. Internet / rider reports: individual experiences."));
			var calls = new List<string>();
			var agent = CreateAgent(handler, _ =>
			{
				calls.Add("web");
				return Task.FromResult("""{"results":[{"title":"Owner experience","url":"https://reviews.example.test/post","content":"Individual experience, not a specification"}]}""");
			}, _ =>
			{
				calls.Add("manual");
				return Task.FromResult("Applicable manual passage");
			});
			await CreateManager(agent, new InMemoryChatStore())
				.AskMechanicBro(new ChatContext(1, ManualBike, 1), new ChatInput("Manual guidance and real-world feedback?", null));

			Assert.Equal(new[] { "manual", "web" }, calls);
			var instructions = handler.Requests[0].GetProperty("messages")[0].GetProperty("content").GetString()!;
			Assert.Contains("MANUAL FIRST, MANUAL ONLY NAHI", instructions);
			Assert.Contains("'Manual se' aur 'Internet / rider reports'", instructions);
			Assert.Contains("General chat ya unavailable manual mein internet search allowed hai", instructions);
			Assert.Contains("Source/link nahi mila toh limitation batao", instructions);
			Assert.Contains("safety-critical procedure override mat karo", instructions);
			Assert.All(handler.Requests, AssertValidSequence);
		}

		[Fact]
		public async Task ProductionFactoryExposesOnlyUserQueryAndExecutesTheRealManualMethod()
		{
			using var handler = new RecordingHandler(
				ToolReply(Call("manual", "SearchManualAsync", """{"userQuery":""}""", "signature")));
			using var httpClient = new HttpClient(new RecordingHandler());
			var config = Configuration();
			var vector = new VectorDbService(config);
			Assert.Equal(new[] { "userQuery", "manualKey", "cancellationToken" },
				vector.SearchManualAsyncFunctionContract.Parameters!.Select(parameter => parameter.Name));
			var agent = MechanicBroAgent.Create(CreateClient(handler),
				new TavilySearchService(config, httpClient), vector);
			var store = new InMemoryChatStore();

			var error = await Assert.ThrowsAsync<ArgumentException>(() => CreateManager(agent, store)
				.AskMechanicBro(new ChatContext(1, ManualBike, 1), new ChatInput("Search", null)));
			Assert.Equal("userQuery", error.ParamName);
			Assert.Empty(store.GetHistory(1));
			var request = Assert.Single(handler.Requests);
			var manual = request.GetProperty("tools").EnumerateArray()
				.Select(tool => tool.GetProperty("function"))
				.Single(function => function.GetProperty("name").GetString() == "SearchManualAsync");
			var schema = manual.GetProperty("parameters");
			Assert.Contains("verified manual", manual.GetProperty("description").GetString());
			var property = Assert.Single(schema.GetProperty("properties").EnumerateObject());
			Assert.Equal("userQuery", property.Name);
			Assert.Equal("string", property.Value.GetProperty("type").GetString());
			Assert.Equal("userQuery", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
			Assert.Equal(3, vector.SearchManualAsyncFunctionContract.Parameters!.Count());
		}

		[Fact]
		public async Task ManualExecutionReceivesServerKeyAndRequestTokenNotModelSuppliedValues()
		{
			using var handler = new RecordingHandler(
				ToolReply(Call("manual", "SearchManualAsync", """{"userQuery":"maintenance","manualKey":"another-manual"}""", "signature")),
				TextReply("Answer"));
			string? executedKey = null;
			CancellationToken executedToken = default;
			var agent = MechanicBroAgent.CreateCore(CreateClient(handler), [Contract("SearchManualAsync", "userQuery")],
				new Dictionary<string, Func<string, string?, CancellationToken, Task<string>>>
				{
					["SearchManualAsync"] = (_, key, token) =>
					{
						executedKey = key;
						executedToken = token;
						return Task.FromResult("Selected manual passage");
					}
				});
			using var cancellation = new CancellationTokenSource();
			await CreateManager(agent, new InMemoryChatStore())
				.AskMechanicBro(new ChatContext(1, ManualBike, 1), new ChatInput("Search", null), cancellation.Token);
			Assert.Equal(ManualBike.ManualKey, executedKey);
			Assert.Equal(cancellation.Token, executedToken);
			Assert.All(handler.Requests, AssertValidSequence);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData(" ")]
		public async Task EmptyToolResultsAreRejectedWithoutSavingAPartialTurn(string? result)
		{
			using var handler = new RecordingHandler(
				ToolReply(Call("web", "SearchInternetAsync", """{"query":"search"}""", "signature")));
			var store = new InMemoryChatStore();
			var agent = CreateAgent(handler, _ => Task.FromResult(result!));

			var error = await Assert.ThrowsAsync<InvalidOperationException>(
				() => CreateManager(agent, store).AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Search", null)));
			Assert.Contains("empty result", error.Message);
			Assert.Empty(store.GetHistory(1));
			Assert.Single(handler.Requests);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task UnknownToolsCannotLeaveAnUnpairedCallInHistory(bool includeKnownTool)
		{
			var calls = new List<object> { Call("unknown", "MissingTool", "{}", "signature") };
			if (includeKnownTool)
			{
				calls.Add(Call("known", "SearchInternetAsync", """{"query":"search"}"""));
			}
			using var handler = new RecordingHandler(ToolReply(calls.ToArray()), TextReply("Recovered"));
			var store = new InMemoryChatStore();
			var manager = CreateManager(CreateAgent(handler), store);

			await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Failed turn", null)));
			Assert.Empty(store.GetHistory(1));
			Assert.Equal(new ChatReply(1, "Recovered"), await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("New turn", null)));
			Assert.Equal(new[] { "system", "user" }, Roles(handler.Requests[1]));
		}

		[Fact]
		public async Task ProviderFailureAfterToolsPreservesTheOriginalErrorAndPreviousHistory()
		{
			using var handler = new RecordingHandler(
				TextReply("Saved answer"),
				ToolReply(Call("web", "SearchInternetAsync", """{"query":"search"}""", "signature")),
				"""{"error":{"message":"Invalid tool exchange","type":"invalid_request_error","code":"invalid_argument"}}""",
				TextReply("Recovered"));
			handler.StatusCodes[2] = HttpStatusCode.BadRequest;
			var store = new InMemoryChatStore();
			var manager = CreateManager(CreateAgent(handler), store);
			await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Saved question", null));

			var error = await Assert.ThrowsAsync<ClientResultException>(
				() => manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Failed question", null)));
			Assert.Equal(400, error.Status);
			Assert.Equal(2, store.GetHistory(1).Count);
			Assert.Equal(new ChatReply(1, "Recovered"), await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Retry", null)));
			Assert.Equal(new[] { "system", "user", "assistant", "user" }, Roles(handler.Requests[3]));
			Assert.DoesNotContain("Failed question", handler.Requests[3].GetRawText());
		}

		[Fact]
		public async Task FiveToolRoundsAllowAFinalAnswerButNeverExecuteASixthBatch()
		{
			var responses = Enumerable.Range(1, 6)
				.Select(i => ToolReply(Call($"call-{i}", "SearchInternetAsync", """{"query":"search"}""", $"signature-{i}")))
				.ToArray();
			using var handler = new RecordingHandler(responses);
			var executions = 0;
			var agent = CreateAgent(handler, _ =>
			{
				executions++;
				return Task.FromResult("result");
			});
			var store = new InMemoryChatStore();
			var error = await Assert.ThrowsAsync<ChatLimitExceededException>(
				() => CreateManager(agent, store).AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Search", null)));

			Assert.Contains("5 rounds", error.Message);
			Assert.Equal(5, executions);
			Assert.Equal(6, handler.Requests.Count);
			Assert.Empty(store.GetHistory(1));
		}

		[Fact]
		public async Task FinalAnswerAfterFiveToolRoundsIsSaved()
		{
			var responses = Enumerable.Range(1, 5)
				.Select(i => ToolReply(Call($"call-{i}", "SearchManualAsync", """{"userQuery":"search"}""", $"signature-{i}")))
				.Append(TextReply("Done")).ToArray();
			using var handler = new RecordingHandler(responses);
			var store = new InMemoryChatStore();

			Assert.Equal(new ChatReply(1, "Done"), await CreateManager(CreateAgent(handler), store).AskMechanicBro(new ChatContext(1, ManualBike, 1), new ChatInput("Search", null)));
			Assert.Equal(7, store.GetHistory(1).Count);
			Assert.All(handler.Requests, request => AssertValidSequence(request));
		}

		[Fact]
		public async Task ToolRoundBudgetIsConfigurableAndResetsPerUserMessage()
		{
			using var handler = new RecordingHandler(
				ToolReply(Call("first", "SearchInternetAsync", """{"query":"search"}""", "signature")),
				TextReply("First"), ToolReply(Call("second", "SearchInternetAsync", """{"query":"search"}""", "signature-2")),
				TextReply("Second"));
			var manager = CreateManager(CreateAgent(handler), new InMemoryChatStore(), maxToolRounds: 1);

			Assert.Equal(new ChatReply(1, "First"), await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("First", null)));
			Assert.Equal(new ChatReply(1, "Second"), await manager.AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Second", null)));
			Assert.Equal(4, handler.Requests.Count);
		}

		[Fact]
		public async Task GreetingDoesNotRunToolsAndPngImageKeepsItsMediaType()
		{
			using var handler = new RecordingHandler(TextReply("Hi bro"));
			var agent = CreateAgent(handler, _ => throw new InvalidOperationException("No tool expected"));
			var image = $"data:image/png;base64,{Convert.ToBase64String(ImageFixtures.Png())}";
			var store = new InMemoryChatStore();

			Assert.Equal(new ChatReply(1, "Hi bro"), await CreateManager(agent, store).AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Hi", image)));
			var content = handler.Requests[0].GetProperty("messages")[1].GetProperty("content");
			Assert.Equal(image, content[1].GetProperty("image_url").GetProperty("url").GetString());
			Assert.Equal(2, store.GetHistory(1).Count);
		}

		[Fact]
		public async Task InvalidChatContextIsRejectedBeforeCallingTheProvider()
		{
			using var handler = new RecordingHandler();
			await Assert.ThrowsAsync<ChatInputException>(
				() => CreateManager(CreateAgent(handler), new InMemoryChatStore())
					.AskMechanicBro(new ChatContext(-1, null, 1), new ChatInput("Hi", null)));
			Assert.Empty(handler.Requests);
		}

		[Theory]
		[InlineData("null")]
		[InlineData("[]")]
		[InlineData("{")]
		public async Task MalformedToolArgumentsFailBeforeExecutingTheTool(string arguments)
		{
			using var handler = new RecordingHandler(ToolReply(Call("web", "SearchInternetAsync", arguments, "signature")));
			var executions = 0;
			var agent = CreateAgent(handler, _ =>
			{
				executions++;
				return Task.FromResult("result");
			});
			var store = new InMemoryChatStore();

			await Assert.ThrowsAnyAsync<JsonException>(() => CreateManager(agent, store).AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Search", null)));
			Assert.Equal(0, executions);
			Assert.Empty(store.GetHistory(1));
		}

		[Theory]
		[InlineData("")]
		[InlineData("duplicate")]
		public async Task MissingOrDuplicateToolIdsAreNotSentBackToTheProvider(string id)
		{
			using var handler = new RecordingHandler(ToolReply(
				Call(id, "SearchInternetAsync", """{"query":"one"}""", "signature"),
				Call(id, "SearchInternetAsync", """{"query":"two"}""")));
			var store = new InMemoryChatStore();

			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				CreateManager(CreateAgent(handler), store).AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Search", null)));
			Assert.Single(handler.Requests);
			Assert.Empty(store.GetHistory(1));
		}

		[Theory]
		[InlineData("")]
		[InlineData(" ")]
		public async Task EmptyFinalAnswersAreNotSaved(string text)
		{
			using var handler = new RecordingHandler(TextReply(text));
			var store = new InMemoryChatStore();
			await Assert.ThrowsAsync<InvalidOperationException>(() =>
				CreateManager(CreateAgent(handler), store).AskMechanicBro(new ChatContext(1, null, 1), new ChatInput("Hi", null)));
			Assert.Empty(store.GetHistory(1));
		}

		internal static IConfiguration Configuration(int maxToolRounds = 5) =>
			new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
			{
				["API_Keys:Gemini_Api_key"] = "test-key",
				["API_Keys:Tavily_Api_key"] = "test-key",
				["Qdrant_Vector_DB:Cluster_Endpoint"] = "localhost",
				["Qdrant_Vector_DB:API_Key"] = "test-key",
				["Qdrant_Vector_DB:ManualCollection"] = "test-manuals",
				["Chat:MaxToolRounds"] = maxToolRounds.ToString()
			}).Build();

		internal static BikeContext ManualBike => new(1, "Harley-Davidson", "X440", 2024, "x440-2024-test");

		internal static AiManagerService CreateManager(IAgent agent, InMemoryChatStore store,
			int maxToolRounds = 5, int maxToolCalls = 6, int maxHistoryTurns = 10)
		{
			var limits = new ChatLimitsOptions
			{
				MaxToolRounds = maxToolRounds,
				MaxToolCallsPerRequest = maxToolCalls,
				MaxHistoryTurns = maxHistoryTurns
			};
			return new(agent, store, limits, NullLogger<AiManagerService>.Instance);
		}

		internal static IAgent CreateAgent(RecordingHandler handler,
			Func<string, Task<string>>? internet = null, Func<string, Task<string>>? manual = null) =>
			MechanicBroAgent.CreateCore(CreateClient(handler),
			[
				Contract("SearchInternetAsync", "query"),
				Contract("SearchManualAsync", "userQuery")
			], new Dictionary<string, Func<string, string?, CancellationToken, Task<string>>>
			{
				["SearchInternetAsync"] = (args, _, _) => internet is null ? Task.FromResult("Internet result") : internet(args),
				["SearchManualAsync"] = (args, _, _) => manual is null ? Task.FromResult("Manual result") : manual(args)
			});

		private static FunctionContract Contract(string name, string parameter) => new()
		{
			Name = name,
			Description = name,
			ReturnType = typeof(Task<string>),
			Parameters =
			[
				new FunctionParameterContract { Name = parameter, ParameterType = typeof(string), IsRequired = true }
			]
		};

		internal static ChatClient CreateClient(RecordingHandler handler) =>
			CreateOpenAIClient(handler).GetChatClient("gemini-3.1-flash-lite");

		internal static OpenAIClient CreateOpenAIClient(RecordingHandler handler) =>
			new OpenAIClient(new ApiKeyCredential("test-key"), new OpenAIClientOptions
			{
				Endpoint = new Uri("https://gemini.test/v1beta/openai/"),
				Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
				RetryPolicy = new ClientRetryPolicy(0)
			});

		internal static object Call(string id, string name, string arguments, string? signature = null)
		{
			var call = new Dictionary<string, object>
			{
				["id"] = id,
				["type"] = "function",
				["function"] = new { name, arguments }
			};
			if (signature is not null)
			{
				call["extra_content"] = new { google = new { thought_signature = signature } };
			}
			return call;
		}

		internal static string ToolReply(params object[] calls) => Completion(
			new { role = "assistant", content = (string?)null, tool_calls = calls }, "tool_calls");

		internal static string TextReply(string text) => Completion(new { role = "assistant", content = text }, "stop");

		private static string Completion(object message, string finishReason) => JsonSerializer.Serialize(new
		{
			id = "test-completion",
			@object = "chat.completion",
			created = 1_700_000_000,
			model = "gemini-3.1-flash-lite",
			choices = new[] { new { index = 0, message, finish_reason = finishReason } }
		});

		private static string? Signature(JsonElement message, int toolIndex) =>
			message.GetProperty("tool_calls")[toolIndex].GetProperty("extra_content")
				.GetProperty("google").GetProperty("thought_signature").GetString();

		private static string?[] Roles(JsonElement request) =>
			request.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("role").GetString()).ToArray();

		internal static void AssertValidSequence(JsonElement request)
		{
			var pending = new HashSet<string>();
			foreach (var message in request.GetProperty("messages").EnumerateArray())
			{
				if (message.GetProperty("role").GetString() == "tool")
				{
					Assert.True(pending.Remove(message.GetProperty("tool_call_id").GetString()!));
					using var result = JsonDocument.Parse(message.GetProperty("content").GetString()!);
					Assert.Equal(JsonValueKind.String, result.RootElement.GetProperty("result").ValueKind);
					continue;
				}
				Assert.Empty(pending);
				if (message.TryGetProperty("tool_calls", out var calls))
				{
					foreach (var call in calls.EnumerateArray())
					{
						Assert.True(pending.Add(call.GetProperty("id").GetString()!));
					}
				}
			}
			Assert.Empty(pending);
		}
	}

	internal sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
	{
		public List<JsonElement> Requests { get; } = [];
		public Dictionary<int, HttpStatusCode> StatusCodes { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var index = Requests.Count;
			using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
			Requests.Add(document.RootElement.Clone());
			Assert.True(index < responses.Length, "An unexpected HTTP request was made.");
			return new HttpResponseMessage(StatusCodes.GetValueOrDefault(index, HttpStatusCode.OK))
			{
				Content = new StringContent(responses[index], Encoding.UTF8, "application/json")
			};
		}
	}
}
