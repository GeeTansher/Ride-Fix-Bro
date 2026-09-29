using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RideFixBro.API.Models;
using RideFixBro.API.Services;
using RideFixBro.Data.Entities;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;

namespace RideFixBro.API.Tests;

public class AdminBikePublicationTests
{
    [Fact]
    public async Task UploadRequiresSqlAdminAndIgnoresClientAdminClaims()
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var anonymous = factory.Client(false);
        using var unauthenticated = await anonymous.PostAsync("/api/admin/bikes", Form());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var user = factory.Client();
        user.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(overrides: new()
        {
            [ClaimTypes.Role] = "Admin",
            ["user_metadata"] = new Dictionary<string, object> { ["role"] = "Admin" }
        }));
        using var forbidden = await user.PostAsync("/api/admin/bikes", Form());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(0, publisher.Calls);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().MasterBikes.ToListAsync());
    }

    [Fact]
    public async Task CatalogEntryIsSavedOnlyAfterPdfPublicationAndSameDetailsReupload()
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        publisher.BeforePublish = async () =>
        {
            using var scope = factory.Services.CreateScope();
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().MasterBikes.ToListAsync());
        };
        using var created = await admin.PostAsync("/api/admin/bikes", Form(skip: 1));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = (await created.Content.ReadFromJsonAsync<BikePublicationResponse>())!;
        Assert.Equal("x440-2024", first.ManualKey);
        Assert.Equal(1, first.SkippedPages);
        Assert.Equal(1, first.Chunks);
        publisher.BeforePublish = null;
        using var replaced = await admin.PostAsync("/api/admin/bikes", Form());
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(first.Bike.Id, (await replaced.Content.ReadFromJsonAsync<BikePublicationResponse>())!.Bike.Id);
        Assert.Equal(2, publisher.Calls);
        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
        Assert.Equal("x440-2024", (await db.MasterBikes.SingleAsync()).ManualKey);
        Assert.Empty(await db.UserBikes.ToListAsync());
    }

    [Theory]
    [InlineData("make")]
    [InlineData("model")]
    [InlineData("year")]
    [InlineData("manualKey")]
    [InlineData("file")]
    public async Task MissingRequiredFieldCannotCreateABike(string missing)
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        using var response = await admin.PostAsync("/api/admin/bikes", Form(omit: missing));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, publisher.Calls);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().MasterBikes.ToListAsync());
    }

    [Fact]
    public async Task FailedPdfPublicationLeavesNoCatalogEntry()
    {
        var publisher = new FakePublisher { Failure = new IOException("Simulated Qdrant outage") };
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        using var response = await admin.PostAsync("/api/admin/bikes", Form());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().MasterBikes.ToListAsync());
    }

    [Fact]
    public async Task CatalogSaveFailureIsNotReportedAsPublicationSuccess()
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER RejectBike BEFORE INSERT ON MasterBikes BEGIN SELECT RAISE(ABORT, 'test-only failure'); END;");
        using var response = await admin.PostAsync("/api/admin/bikes", Form());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, publisher.Calls);
        using var verification = factory.Services.CreateScope();
        Assert.Empty(await verification.ServiceProvider.GetRequiredService<RideFixBroDbContext>().MasterBikes.ToListAsync());
    }

    [Fact]
    public async Task ExistingBikeCannotBeSilentlyRemappedAndNewBikeCannotOverwriteAnotherKey()
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        using var first = await admin.PostAsync("/api/admin/bikes", Form());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var remapped = await admin.PostAsync("/api/admin/bikes", Form(key: "different-key"));
        Assert.Equal(HttpStatusCode.Conflict, remapped.StatusCode);
        using var wrongYear = await admin.PostAsync("/api/admin/bikes", Form(year: 2026));
        Assert.Equal(HttpStatusCode.Conflict, wrongYear.StatusCode);
        Assert.Equal(1, publisher.Calls);
    }

    private static async Task<HttpClient> AdminClient(ChatApiFactory factory)
    {
        var client = factory.Client();
        using var me = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
        (await db.Users.SingleAsync()).RoleId = 84;
        await db.SaveChangesAsync();
        return client;
    }

    private static MultipartFormDataContent Form(string? omit = null, string key = "x440-2024", int year = 2024, int skip = 0)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["make"] = "Harley-Davidson", ["model"] = "X440", ["year"] = year.ToString(),
            ["manualKey"] = key, ["skipPages"] = skip.ToString()
        })
            if (name != omit) form.Add(new StringContent(value), name);
        if (omit != "file")
        {
            var file = new ByteArrayContent(ManualUploadTests.Pdf("Cover page", "Official manual text"));
            file.Headers.ContentType = new("application/pdf");
            form.Add(file, "file", "manual.pdf");
        }
        return form;
    }

    private sealed class FakePublisher : IManualPublisher
    {
        public int Calls { get; private set; }
        public Func<Task>? BeforePublish { get; set; }
        public Exception? Failure { get; init; }
        public async Task<ManualUploadResult> ReplaceManualAsync(byte[] pdf, string manualKey, int skipPages, CancellationToken token)
        {
            Calls++;
            var chunks = VectorDbService.ExtractChunks(pdf, skipPages, token);
            if (BeforePublish is not null) await BeforePublish();
            if (Failure is not null) throw Failure;
            return new ManualUploadResult(manualKey, chunks.Count, skipPages);
        }
    }
}
