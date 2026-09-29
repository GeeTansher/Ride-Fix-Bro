using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using RideFixBro.API.DataStore.Interfaces;
using RideFixBro.Data.Entities;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace RideFixBro.API.Tests
{
	public class AuthenticationTests
	{
		[Theory]
		[InlineData("/api/me", false)]
		[InlineData("/api/Chat/ask", true)]
		public async Task MissingTokenIsRejectedBeforeCreatingAUserOrCallingTheModel(string path, bool post)
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client(authenticated: false);
			using var response = post
				? await client.PostAsJsonAsync(path, new { sessionId = 0, isGeneral = true, message = "Hi" })
				: await client.GetAsync(path);
			Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
			Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
			Assert.Equal(0, factory.Agent.Calls);
			using var scope = factory.Services.CreateScope();
			Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().Users.ToListAsync());
		}

		[Theory]
		[InlineData("expired")]
		[InlineData("issuer")]
		[InlineData("audience")]
		[InlineData("signature")]
		[InlineData("symmetric")]
		[InlineData("anonymous")]
		[InlineData("service-role")]
		[InlineData("empty-sub")]
		[InlineData("invalid-session")]
		[InlineData("email-too-long")]
		[InlineData("malformed")]
		public async Task InvalidTokensNeverCreateAUser(string scenario)
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client(false);
			using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
			var token = scenario switch
			{
				"expired" => AuthTestTokens.Create(expires: DateTime.UtcNow.AddMinutes(-5)),
				"issuer" => AuthTestTokens.Create(issuer: "https://accounts.google.com"),
				"audience" => AuthTestTokens.Create(audience: "wrong-audience"),
				"signature" => AuthTestTokens.Create(credentials: new SigningCredentials(
					new ECDsaSecurityKey(otherKey) { KeyId = AuthTestTokens.SigningKey.KeyId }, SecurityAlgorithms.EcdsaSha256)),
				"symmetric" => AuthTestTokens.Create(credentials: new SigningCredentials(
					new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256)),
				"anonymous" => AuthTestTokens.Create(overrides: new() { ["is_anonymous"] = true }),
				"service-role" => AuthTestTokens.Create(overrides: new() { ["role"] = "service_role" }),
				"empty-sub" => AuthTestTokens.Create(userId: Guid.Empty),
				"invalid-session" => AuthTestTokens.Create(overrides: new() { ["session_id"] = "not-a-guid" }),
				"email-too-long" => AuthTestTokens.Create(overrides: new() { ["email"] = new string('a', 256) }),
				_ => "not.a.jwt"
			};
			client.DefaultRequestHeaders.Authorization = new("Bearer", token);
			using var response = await client.GetAsync("/api/me");
			Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
			using var scope = factory.Services.CreateScope();
			Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().Users.ToListAsync());
		}

		[Fact]
		public async Task FirstLoginCreatesOneDefaultUserAndIgnoresIncomingAdminClaims()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			client.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(overrides: new()
			{
				[ClaimTypes.Role] = "Admin",
				["app_user_id"] = "999",
				["user_metadata"] = new Dictionary<string, object> { ["role"] = "Admin" }
			}));
			using var first = await client.GetAsync("/api/me");
			Assert.Equal(HttpStatusCode.OK, first.StatusCode);
			var profile = await first.Content.ReadFromJsonAsync<JsonElement>();
			Assert.Equal("User", profile.GetProperty("role").GetString());
			Assert.NotEqual(999, profile.GetProperty("id").GetInt32());
			using var second = await client.GetAsync("/api/me");
			Assert.Equal(HttpStatusCode.OK, second.StatusCode);
			using var admin = await client.GetAsync("/test/admin");
			Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);

			using var scope = factory.Services.CreateScope();
			var row = Assert.Single(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().Users.ToListAsync());
			Assert.Equal(42, row.RoleId);
			Assert.Equal(AuthTestTokens.UserId, row.SupabaseUserId);
		}

		[Fact]
		public async Task SqlRoleChangesApplyOnTheNextRequestWithoutReissuingTheToken()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			using var first = await client.GetAsync("/api/me");
			Assert.Equal(HttpStatusCode.OK, first.StatusCode);
			using (var scope = factory.Services.CreateScope())
			{
				var database = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
				var user = await database.Users.SingleAsync();
				user.RoleId = 84;
				await database.SaveChangesAsync();
			}
			using var allowed = await client.GetAsync("/test/admin");
			Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
			using (var scope = factory.Services.CreateScope())
			{
				var database = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
				var user = await database.Users.SingleAsync();
				user.RoleId = 42;
				await database.SaveChangesAsync();
			}
			using var denied = await client.GetAsync("/test/admin");
			Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
		}

		[Fact]
		public async Task SameSessionIdAndEmailDoNotShareUserHistoryOrProfiles()
		{
			await using var factory = new ChatApiFactory();
			using var first = factory.Client();
			using var second = factory.Client();
			var secondUser = Guid.NewGuid();
			second.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(secondUser, new()
			{
				["email"] = $"{AuthTestTokens.UserId:N}@example.test"
			}));
			var firstId = await factory.CreateChatAsync(first);
			var secondId = await factory.CreateChatAsync(second);
			using var firstReply = await first.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = firstId, message = "My private question", userId = secondUser });
			using var secondReply = await second.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = secondId, message = "Other question", userId = AuthTestTokens.UserId });
			Assert.Equal(HttpStatusCode.OK, firstReply.StatusCode);
			Assert.Equal(HttpStatusCode.OK, secondReply.StatusCode);

			Assert.Equal(2, factory.History(firstId).Count);
			Assert.Equal(2, factory.History(secondId, secondUser).Count);
			using var denied = await second.PostAsJsonAsync("/api/Chat/ask",
				new { sessionId = firstId, message = "Try another user's chat" });
			Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
			Assert.StartsWith("My private question", Assert.IsType<AutoGen.Core.TextMessage>(factory.History(firstId)[0]).Content);
			Assert.StartsWith("Other question", Assert.IsType<AutoGen.Core.TextMessage>(factory.History(secondId, secondUser)[0]).Content);
			using var scope = factory.Services.CreateScope();
			Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().Users.CountAsync());
		}

		[Fact]
		public async Task MissingDefaultRoleDoesNotCreateAnAdministratorOrAnUnassignedUser()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			using (var scope = factory.Services.CreateScope())
			{
				var database = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
				database.MasterUserRoles.Remove(await database.MasterUserRoles.SingleAsync(role => role.RoleName == "User"));
				await database.SaveChangesAsync();
			}
			using var response = await client.GetAsync("/api/me");
			Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
			using var verification = factory.Services.CreateScope();
			Assert.Empty(await verification.ServiceProvider.GetRequiredService<RideFixBroDbContext>().Users.ToListAsync());
		}

		[Fact]
		public async Task DatabaseFailureIs503AndNeverAnAnonymousSuccess()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			using (var scope = factory.Services.CreateScope())
			{
				await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>()
					.Database.ExecuteSqlRawAsync("DROP TABLE Users");
			}
			using var response = await client.GetAsync("/api/me");
			Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
			var text = await response.Content.ReadAsStringAsync();
			Assert.DoesNotContain("SQLite", text);
			Assert.DoesNotContain("SELECT", text);
		}
	}

	[ApiController]
	[Route("test/admin")]
	public class AdminTestController : ControllerBase
	{
		[Authorize(Roles = "Admin")]
		[HttpGet]
		public IActionResult Get() => Ok();
	}
}
