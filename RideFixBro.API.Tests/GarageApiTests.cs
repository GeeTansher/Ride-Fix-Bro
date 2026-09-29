using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RideFixBro.API.Models;
using RideFixBro.Data.Entities;
using System.Net;
using System.Net.Http.Json;

namespace RideFixBro.API.Tests
{
	public class GarageApiTests
	{
		[Theory]
		[InlineData("/api/bikes", false)]
		[InlineData("/api/garage", false)]
		[InlineData("/api/garage", true)]
		public async Task EndpointsRequireLogin(string path, bool post)
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client(false);
			using var response = post
				? await client.PostAsJsonAsync(path, new { bikeId = 1 })
				: await client.GetAsync(path);
			Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		}

		[Fact]
		public async Task CatalogReturnsOnlyStoredModelsAndYearsWithoutCreatingGarageEntries()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			await SeedCatalog(factory);

			var bikes = await client.GetFromJsonAsync<List<BikeResponse>>("/api/bikes");
			Assert.NotNull(bikes);
			Assert.Equal(new[] { 2024, 2025 }, bikes.Select(bike => bike.Year));
			Assert.All(bikes, bike => Assert.Equal("X440", bike.Model));
			Assert.Empty((await client.GetFromJsonAsync<List<GarageBikeResponse>>("/api/garage"))!);
		}

		[Fact]
		public async Task AddIsIdempotentPerCatalogEntryAndAnotherYearIsAllowed()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			await SeedCatalog(factory);
			using var first = await client.PostAsJsonAsync("/api/garage", new { bikeId = 101 });
			using var repeated = await client.PostAsJsonAsync("/api/garage", new { bikeId = 101 });
			using var anotherYear = await client.PostAsJsonAsync("/api/garage", new { bikeId = 102 });

			Assert.Equal(HttpStatusCode.Created, first.StatusCode);
			Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
			Assert.Equal(HttpStatusCode.Created, anotherYear.StatusCode);
			var firstBike = await first.Content.ReadFromJsonAsync<GarageBikeResponse>();
			Assert.Equal(firstBike, await repeated.Content.ReadFromJsonAsync<GarageBikeResponse>());
			var garage = await client.GetFromJsonAsync<List<GarageBikeResponse>>("/api/garage");
			Assert.Equal(new[] { 2024, 2025 }, garage!.Select(bike => bike.Year));
		}

		[Fact]
		public async Task UsersSeeOnlyTheirGarageAndCannotSelectAnOwnerInTheBody()
		{
			await using var factory = new ChatApiFactory();
			using var first = factory.Client();
			using var second = factory.Client();
			second.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(Guid.NewGuid()));
			await SeedCatalog(factory);
			using var me = await second.GetAsync("/api/me");
			Assert.Equal(HttpStatusCode.OK, me.StatusCode);
			using var created = await first.PostAsJsonAsync("/api/garage", new
			{
				bikeId = 101, userId = 2, role = "Admin", make = "Fake", model = "Fake", year = 2099
			});
			Assert.Equal(HttpStatusCode.Created, created.StatusCode);
			var bike = await created.Content.ReadFromJsonAsync<GarageBikeResponse>();
			Assert.Equal("Harley-Davidson", bike!.Make);
			Assert.Equal(2024, bike.Year);
			Assert.Single((await first.GetFromJsonAsync<List<GarageBikeResponse>>("/api/garage"))!);
			Assert.Empty((await second.GetFromJsonAsync<List<GarageBikeResponse>>("/api/garage"))!);
			using var secondBike = await second.PostAsJsonAsync("/api/garage", new { bikeId = 101 });
			Assert.Equal(HttpStatusCode.Created, secondBike.StatusCode);
			Assert.NotEqual(bike.Id, (await secondBike.Content.ReadFromJsonAsync<GarageBikeResponse>())!.Id);
		}

		[Theory]
		[InlineData(0, HttpStatusCode.BadRequest)]
		[InlineData(-1, HttpStatusCode.BadRequest)]
		[InlineData(999, HttpStatusCode.NotFound)]
		public async Task InvalidOrMissingCatalogIdDoesNotCreateAnything(int bikeId, HttpStatusCode expected)
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			await SeedCatalog(factory);
			using var response = await client.PostAsJsonAsync("/api/garage", new { bikeId });
			Assert.Equal(expected, response.StatusCode);
			Assert.Empty((await client.GetFromJsonAsync<List<GarageBikeResponse>>("/api/garage"))!);
		}

		[Fact]
		public async Task DatabaseUniqueIndexAlsoBlocksDuplicateGarageRows()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			await SeedCatalog(factory);
			using var added = await client.PostAsJsonAsync("/api/garage", new { bikeId = 101 });
			Assert.Equal(HttpStatusCode.Created, added.StatusCode);
			using var scope = factory.Services.CreateScope();
			var database = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
			var entry = await database.UserBikes.AsNoTracking().SingleAsync();
			database.UserBikes.Add(new UserBike
			{
				UserId = entry.UserId, BikeId = entry.BikeId, CreatedAt = DateTime.UtcNow
			});
			await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
		}

		[Fact]
		public async Task CatalogUniqueIndexRejectsAnIdenticalMakeModelAndYear()
		{
			await using var factory = new ChatApiFactory();
			using var client = factory.Client();
			await SeedCatalog(factory);
			using var scope = factory.Services.CreateScope();
			var database = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
			database.MasterBikes.Add(new MasterBike { Make = "Harley-Davidson", Model = "X440", Year = 2024 });
			await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
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
