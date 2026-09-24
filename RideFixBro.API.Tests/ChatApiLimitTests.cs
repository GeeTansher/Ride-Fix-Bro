using AutoGen.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.API.Services;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RideFixBro.API.Tests
{
	public class ChatApiLimitTests
	{
		[Theory]
		[InlineData("/api/Chat/upload-dummy-manual")]
		[InlineData("/api/Chat/upload-pdf-manual")]
		public async Task UploadRoutesStayUnavailableEvenIfCallerClaimsToBeAdmin(string route)
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			client.DefaultRequestHeaders.Add("X-Admin-Code", "not-a-real-secret");
			using var response = await client.PostAsJsonAsync(route, new { isAdmin = true });
			Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
			Assert.Equal(0, factory.Agent.Calls);
		}

		[Fact]
		public async Task AllowsFiveRequestsAndRejectsTheSixthAcrossDifferentSessions()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			for (var i = 0; i < 6; i++)
			{
				using var other = await client.PostAsJsonAsync("/test/unrestricted", new { message = "Other API" });
				Assert.Equal(HttpStatusCode.OK, other.StatusCode);
			}
			for (var i = 1; i <= 5; i++)
			{
				using var response = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = $"session-{i}", message = "Hi" });
				Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			}

			client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.1");
			using var rejected = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = "different", message = "Hi" });
			Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
			Assert.NotNull(rejected.Headers.RetryAfter);
			Assert.False(string.IsNullOrWhiteSpace(await Error(rejected)));
			Assert.Equal(5, factory.Agent.Calls);
			Assert.Empty(factory.Services.GetRequiredService<IChatHistoryStore>().GetHistory("different"));
			using var unaffected = await client.PostAsJsonAsync("/test/unrestricted", new { message = "Still available" });
			Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
		}

		[Fact]
		public async Task RejectsConcurrentChatsWithoutQueueingAndCountsThemTowardsRateLimit()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			factory.Agent.Reply = async cancellationToken =>
			{
				entered.TrySetResult();
				await release.Task.WaitAsync(cancellationToken);
				return new TextMessage(Role.Assistant, "Done", "MechanicBro");
			};
			var first = client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = "first", message = "Hi" });
			try
			{
				await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
				using var second = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = "second", message = "Hi" })
					.WaitAsync(TimeSpan.FromSeconds(5));
				Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
				Assert.Equal(1, factory.Agent.Calls);
				using var other = await client.PostAsJsonAsync("/test/unrestricted", new { message = "Other API" })
					.WaitAsync(TimeSpan.FromSeconds(5));
				Assert.Equal(HttpStatusCode.OK, other.StatusCode);
			}
			finally
			{
				release.TrySetResult();
			}

			using var completed = await first.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
			using var next = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = "second", message = "Hi" });
			Assert.Equal(HttpStatusCode.OK, next.StatusCode);

			for (var i = 0; i < 2; i++)
			{
				using var accepted = await client.PostAsJsonAsync("/api/Chat/ask",
					new { sessionId = $"extra-{i}", message = "Hi" });
				Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
			}
			using var sixth = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = "sixth", message = "Hi" });
			Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
			Assert.NotNull(sixth.Headers.RetryAfter);
			Assert.Equal(4, factory.Agent.Calls);
		}

		[Fact]
		public async Task MessageLengthBoundaryIsEnforcedBeforeCallingTheAgent()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			using var accepted = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "session", message = new string('a', 2000) });
			Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
			using var rejected = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "session", message = new string('a', 2001) });
			Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
			Assert.Contains("2000", await Error(rejected));
			Assert.Equal(1, factory.Agent.Calls);
			Assert.Equal(2, factory.Services.GetRequiredService<IChatHistoryStore>().GetHistory("session").Count);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task RequestBodyLimitChecksActualBytesEvenWithoutContentLength(bool unknownLength)
		{
			await using var factory = new ChatApiFactory();
			factory.UseKestrel(0);
			using var client = factory.CreateClient();
			var json = """{"sessionId":"session","message":"Hi"}""";
			var exactLimit = json.PadRight(3 * 1024 * 1024);
			using var accepted = await SendBody(client, exactLimit, unknownLength);
			Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
			using var rejected = await SendBody(client, exactLimit + " ", unknownLength);
			Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
			Assert.False(string.IsNullOrWhiteSpace(await Error(rejected)));
			Assert.Equal(1, factory.Agent.Calls);
			using var unaffected = await SendBody(client, exactLimit + " ", unknownLength, "/test/unrestricted");
			Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
		}

		[Fact]
		public async Task InvalidAndOversizedImagesNeverReachTheAgent()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			using var invalid = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = "session", message = "Photo", imageData = "not an image" });
			Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
			using var oversized = await client.PostAsJsonAsync("/api/Chat/ask", new
			{
				sessionId = "session",
				message = "Photo",
				imageData = Convert.ToBase64String(new byte[2 * 1024 * 1024 + 1])
			});
			Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
			Assert.Equal(0, factory.Agent.Calls);
		}

		[Fact]
		public async Task AValidPhotoReachesTheAgent()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			using var response = await client.PostAsJsonAsync("/api/Chat/ask", new
			{
				sessionId = "session",
				message = "Photo",
				imageData = ImageFixtures.JpegBase64
			});
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			Assert.Equal(1, factory.Agent.Calls);
			Assert.IsType<MultiModalMessage>(factory.Services.GetRequiredService<IChatHistoryStore>().GetHistory("session")[0]);
		}

		[Fact]
		public async Task ToolBudgetErrorsAreExplicitAndInternalErrorsAreNotExposed()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			factory.Agent.Reply = _ => throw new ChatLimitExceededException("Bhai, tool budget khatam.");
			using var limited = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = "session", message = "Hi" });
			Assert.Equal(HttpStatusCode.UnprocessableEntity, limited.StatusCode);
			Assert.Contains("tool budget", await Error(limited));

			factory.Agent.Reply = _ => throw new InvalidOperationException("private-provider-details");
			using var failed = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = "session", message = "Hi" });
			Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
			Assert.DoesNotContain("private-provider-details", await failed.Content.ReadAsStringAsync());
			Assert.Empty(factory.Services.GetRequiredService<IChatHistoryStore>().GetHistory("session"));
		}

		private static async Task<string> Error(HttpResponseMessage response) =>
			(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

		private static async Task<HttpResponseMessage> SendBody(HttpClient client, string body, bool unknownLength,
			string path = "/api/Chat/ask")
		{
			using HttpContent content = unknownLength
				? new UnknownLengthContent(Encoding.UTF8.GetBytes(body))
				: new StringContent(body, Encoding.UTF8, "application/json");
			return await client.PostAsync(path, content);
		}

		private sealed class UnknownLengthContent : HttpContent
		{
			private readonly byte[] _bytes;

			public UnknownLengthContent(byte[] bytes)
			{
				_bytes = bytes;
				Headers.ContentType = new("application/json");
			}

			protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
				stream.WriteAsync(_bytes).AsTask();

			protected override bool TryComputeLength(out long length)
			{
				length = 0;
				return false;
			}
		}
	}

	internal sealed class ChatApiFactory : WebApplicationFactory<Program>
	{
		public DemoAgent Agent { get; } = new();

		public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions
		{
			BaseAddress = new Uri("https://localhost"),
			AllowAutoRedirect = false
		});

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Testing");
			builder.ConfigureServices(services =>
			{
				services.AddControllers().AddApplicationPart(typeof(UnrestrictedTestController).Assembly);
				services.RemoveAll<IAgent>();
				services.AddSingleton<IAgent>(Agent);
			});
		}
	}

	// Test-only API: jaan-boojhkar Ask ke limit attributes nahi lagaye hain.
	[ApiController]
	[Route("test/unrestricted")]
	public class UnrestrictedTestController : ControllerBase
	{
		[HttpPost]
		public IActionResult Post([FromBody] JsonElement body)
		{
			return Ok(new { Accepted = true });
		}
	}

	internal sealed class DemoAgent : IAgent
	{
		private int _calls;
		public int Calls => Volatile.Read(ref _calls);
		public string Name => "MechanicBro";
		public Func<CancellationToken, Task<IMessage>>? Reply { get; set; }

		public Task<IMessage> GenerateReplyAsync(IEnumerable<IMessage> messages, GenerateReplyOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _calls);
			return Reply?.Invoke(cancellationToken)
				?? Task.FromResult<IMessage>(new TextMessage(Role.Assistant, "Hi bro", Name));
		}
	}
}
