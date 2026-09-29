using AutoGen.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RideFixBro.Data.Entities;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.API.DataStore;
using RideFixBro.API.Services;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
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
			var rejectedId = await factory.CreateChatAsync(client);
			for (var i = 0; i < 6; i++)
			{
				using var other = await client.PostAsJsonAsync("/test/unrestricted", new { message = "Other API" });
				Assert.Equal(HttpStatusCode.OK, other.StatusCode);
			}
			for (var i = 1; i <= 5; i++)
			{
				using var response = await client.PostAsJsonAsync("/api/Chat/ask",
					new { sessionId = await factory.CreateChatAsync(client), message = "Hi" });
				Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			}

			client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.1");
			using var rejected = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = rejectedId, message = "Hi" });
			Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
			Assert.NotNull(rejected.Headers.RetryAfter);
			Assert.False(string.IsNullOrWhiteSpace(await Error(rejected)));
			Assert.Equal(5, factory.Agent.Calls);
			Assert.Empty(factory.History(rejectedId));
			using var unaffected = await client.PostAsJsonAsync("/test/unrestricted", new { message = "Still available" });
			Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
		}

		[Fact]
		public async Task RejectsConcurrentChatsWithoutQueueingAndCountsThemTowardsRateLimit()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var firstId = await factory.CreateChatAsync(client);
			var secondId = await factory.CreateChatAsync(client);
			var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			factory.Agent.Reply = async cancellationToken =>
			{
				entered.TrySetResult();
				await release.Task.WaitAsync(cancellationToken);
				return new TextMessage(Role.Assistant, "Done", "MechanicBro");
			};
			var first = client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = firstId, message = "Hi" });
			try
			{
				await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
				using var second = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = secondId, message = "Hi" })
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
			using var next = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = secondId, message = "Hi" });
			Assert.Equal(HttpStatusCode.OK, next.StatusCode);

			for (var i = 0; i < 2; i++)
			{
				using var accepted = await client.PostAsJsonAsync("/api/Chat/ask",
					new { sessionId = secondId, message = "Hi" });
				Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
			}
			using var sixth = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = secondId, message = "Hi" });
			Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
			Assert.NotNull(sixth.Headers.RetryAfter);
			Assert.Equal(4, factory.Agent.Calls);
		}

		[Fact]
		public async Task MessageLengthBoundaryIsEnforcedBeforeCallingTheAgent()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			var id = await factory.CreateChatAsync(client);
			using var accepted = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = id, message = new string('a', 2000) });
			Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
			using var rejected = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = id, message = new string('a', 2001) });
			Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
			Assert.Contains("2000", await Error(rejected));
			Assert.Equal(1, factory.Agent.Calls);
			Assert.Equal(2, factory.History(id).Count);
		}

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public async Task RequestBodyLimitChecksActualBytesEvenWithoutContentLength(bool unknownLength)
		{
			await using var factory = new ChatApiFactory();
			factory.UseKestrel(0);
			using var client = factory.CreateClient();
			var id = await factory.CreateChatAsync(client);
			var json = JsonSerializer.Serialize(new { sessionId = id, message = "Hi" });
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
			var id = await factory.CreateChatAsync(client);
			using var invalid = await client.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = id, message = "Photo", imageData = "not an image" });
			Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
			using var oversized = await client.PostAsJsonAsync("/api/Chat/ask", new
			{
				sessionId = id,
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
			var id = await factory.CreateChatAsync(client);
			using var response = await client.PostAsJsonAsync("/api/Chat/ask", new
			{
				sessionId = id,
				message = "Photo",
				imageData = ImageFixtures.JpegBase64
			});
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			Assert.Equal(1, factory.Agent.Calls);
			Assert.Contains("photo from this earlier turn was not stored", Assert.IsType<TextMessage>(factory.History(id)[0]).Content);
		}

		[Fact]
		public async Task ToolBudgetErrorsAreExplicitAndInternalErrorsAreNotExposed()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			var id = await factory.CreateChatAsync(client);
			factory.Agent.Reply = _ => throw new ChatLimitExceededException("Bhai, tool budget khatam.");
			using var limited = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = id, message = "Hi" });
			Assert.Equal(HttpStatusCode.UnprocessableEntity, limited.StatusCode);
			Assert.Contains("tool budget", await Error(limited));

			factory.Agent.Reply = _ => throw new InvalidOperationException("private-provider-details");
			using var failed = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = id, message = "Hi" });
			Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
			Assert.DoesNotContain("private-provider-details", await failed.Content.ReadAsStringAsync());
			Assert.Empty(factory.History(id));
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
		private readonly SqliteConnection _database = new("Data Source=:memory:");
		private bool _seeded;
		public DemoAgent Agent { get; } = new();

		public async Task<int> CreateChatAsync(HttpClient client, int? bikeId = null)
		{
			using var response = await client.GetAsync("/api/me");
			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			var userId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
			using var scope = Services.CreateScope();
			var context = await scope.ServiceProvider.GetRequiredService<ChatSessionService>()
				.CreateAsync(userId, bikeId, bikeId is null, CancellationToken.None);
			return context.Id;
		}

		public List<IMessage> History(int id, Guid? user = null)
		{
			using var scope = Services.CreateScope();
			var owner = user ?? AuthTestTokens.UserId;
			return scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().Messages.AsNoTracking()
				.Where(row => row.ChatSessionId == id && row.ChatSession.User.SupabaseUserId == owner)
				.OrderBy(row => row.SequenceNumber).AsEnumerable().Select(ChatMessageCodec.Decode).ToList();
		}

		public HttpClient Client(bool authenticated = true)
		{
			var client = CreateClient(new WebApplicationFactoryClientOptions
			{
				BaseAddress = new Uri("https://localhost"),
				AllowAutoRedirect = false
			});
			if (!authenticated)
			{
				client.DefaultRequestHeaders.Authorization = null;
			}
			return client;
		}

		protected override void ConfigureClient(HttpClient client)
		{
			base.ConfigureClient(client);
			if (!_seeded)
			{
				using var scope = Services.CreateScope();
				var database = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
				database.Database.EnsureCreated();
				database.MasterUserRoles.AddRange(
					new MasterUserRole { Id = 42, RoleName = "User", Description = "Test user" },
					new MasterUserRole { Id = 84, RoleName = "Admin", Description = "Test administrator" });
				database.SaveChanges();
				_seeded = true;
			}
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AuthTestTokens.Create());
		}

		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Testing");
			builder.UseSetting("Supabase:ValidIssuer", AuthTestTokens.Issuer);
			builder.UseSetting("Supabase:ValidAudience", "authenticated");
			builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Supabase:ValidIssuer"] = AuthTestTokens.Issuer,
				["Supabase:ValidAudience"] = "authenticated"
			}));
			_database.Open();
			builder.ConfigureServices(services =>
			{
				services.AddControllers().AddApplicationPart(typeof(UnrestrictedTestController).Assembly);
				services.RemoveAll<IAgent>();
				services.AddSingleton<IAgent>(Agent);
				services.RemoveAll<DbContextOptions<RideFixBroDbContext>>();
				services.RemoveAll<IDbContextOptionsConfiguration<RideFixBroDbContext>>();
				services.AddDbContext<RideFixBroDbContext>(options => options.UseSqlite(_database));
				services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, AuthTestTokens.Configure);
			});
		}

		public override async ValueTask DisposeAsync()
		{
			await base.DisposeAsync();
			await _database.DisposeAsync();
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
