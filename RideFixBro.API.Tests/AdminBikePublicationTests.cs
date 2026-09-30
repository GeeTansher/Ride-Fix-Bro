using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RideFixBro.API.Common;
using RideFixBro.API.Models.ManualPublishModels;
using RideFixBro.API.Services;
using RideFixBro.API.Services.BackgroundProcess.ManualPublish;
using RideFixBro.API.Services.BackgroundProcess.ManualPublish.Helper;
using RideFixBro.API.Services.BackgroundProcess.ManualPublish.Interface;
using RideFixBro.Data.Entities;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

namespace RideFixBro.API.Tests;

public class AdminBikePublicationTests
{
    [Fact]
    public async Task SubmissionRequiresSqlAdminAndDoesNotTrustClientRoleClaims()
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var anonymous = factory.Client(false);
        using var unauthenticated = await anonymous.PostAsync("/api/admin/bikes", Form());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var user = factory.Client();
        user.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(overrides: new()
        {
            [ClaimTypes.Role] = "Admin", ["user_metadata"] = new Dictionary<string, object> { ["role"] = "Admin" }
        }));
        using var denied = await user.PostAsync("/api/admin/bikes", Form());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, publisher.Calls);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().ManualPublicationJobs.ToListAsync());
    }

    [Fact]
    public async Task AcceptedJobContainsOnlyTextAndDoesNotPublishWithinTheHttpRequest()
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        using var response = await admin.PostAsync("/api/admin/bikes", Form(skip: 1));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<ManualPublicationJobResponse>())!;
        Assert.Equal("Queued", job.Status);
        Assert.Equal(1, job.SkipPages);
        Assert.Equal(0, job.CompletedChunks);
        Assert.Contains(job.Id.ToString(), response.Headers.Location!.ToString());
        Assert.Equal(0, publisher.Calls);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
        var saved = await db.ManualPublicationJobs.SingleAsync();
        Assert.Contains("Official manual text", saved.ChunksJson);
        Assert.DoesNotContain("Cover page", saved.ChunksJson!);
        Assert.DoesNotContain("%PDF", saved.ChunksJson!);
        Assert.Empty(await db.MasterBikes.ToListAsync());
        var statusJson = await admin.GetStringAsync(response.Headers.Location);
        Assert.DoesNotContain("ChunksJson", statusJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerPublishesThenSavesCatalogAndClearsSourceText()
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        var first = await Submit(admin);
        publisher.BeforePublish = async _ =>
        {
            using var scope = factory.Services.CreateScope();
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().MasterBikes.ToListAsync());
        };
        Assert.True(await factory.ProcessNextPublicationAsync());
        var completed = await Status(admin, first.Id);
        Assert.Equal("Succeeded", completed.Status);
        Assert.Equal(completed.TotalChunks, completed.CompletedChunks);
        Assert.NotNull(completed.BikeId);
        publisher.BeforePublish = null;
        var second = await Submit(admin);
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(await factory.ProcessNextPublicationAsync());
        Assert.Equal(completed.BikeId, (await Status(admin, second.Id)).BikeId);
        using var verification = factory.Services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
        Assert.Single(await db.MasterBikes.ToListAsync());
        Assert.Empty(await db.UserBikes.ToListAsync());
        Assert.All(await db.ManualPublicationJobs.ToListAsync(), row => Assert.Null(row.ChunksJson));
    }

    [Fact]
    public async Task ActiveDuplicateReturnsConflictAndQueueTextIsBounded()
    {
        await using var factory = new ChatApiFactory { ManualPublisher = new FakePublisher() };
        using var admin = await AdminClient(factory);
        await Submit(admin);
        using var duplicate = await admin.PostAsync("/api/admin/bikes", Form());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await Submit(admin, "bike-2025", 2025);
        await Submit(admin, "bike-2026", 2026);
        using var full = await admin.PostAsync("/api/admin/bikes", Form(key: "bike-2027", year: 2027));
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().ManualPublicationJobs.CountAsync());
    }

    [Theory]
    [InlineData("make")]
    [InlineData("model")]
    [InlineData("year")]
    [InlineData("manualKey")]
    [InlineData("file")]
    public async Task MissingRequiredFieldCannotQueueAPublication(string missing)
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        using var response = await admin.PostAsync("/api/admin/bikes", Form(omit: missing));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, publisher.Calls);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().ManualPublicationJobs.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureIsPersistedAsFailedInsteadOfCatalogSuccess(bool quota)
    {
        var publisher = new FakePublisher { Failure = quota
            ? new ChatInputException("Gemini embedding quota is exhausted.", 429) : new IOException("Private provider details") };
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        var job = await Submit(admin);
        await factory.ProcessNextPublicationAsync();
        var status = await Status(admin, job.Id);
        Assert.Equal("Failed", status.Status);
        Assert.NotNull(status.CompletedAt);
        Assert.NotNull(status.Error);
        Assert.DoesNotContain("Private provider", status.Error);
        if (quota) Assert.Contains("Gemini embedding quota", status.Error);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
        Assert.Null((await db.ManualPublicationJobs.SingleAsync()).ChunksJson);
        Assert.Empty(await db.MasterBikes.ToListAsync());
    }

    [Fact]
    public async Task CatalogSaveFailureCannotLeaveSuccessShapedJobState()
    {
        await using var factory = new ChatApiFactory { ManualPublisher = new FakePublisher() };
        using var admin = await AdminClient(factory);
        var job = await Submit(admin);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER RejectBike BEFORE INSERT ON MasterBikes BEGIN SELECT RAISE(ABORT, 'test-only failure'); END;");
        await factory.ProcessNextPublicationAsync();
        Assert.Equal("Failed", (await Status(admin, job.Id)).Status);
        using var verify = factory.Services.CreateScope();
        Assert.Empty(await verify.ServiceProvider.GetRequiredService<RideFixBroDbContext>().MasterBikes.ToListAsync());
    }

    [Fact]
    public async Task HostShutdownLeavesCheckpointAndANewScopeResumesTheSameRevision()
    {
        using var cancellation = new CancellationTokenSource();
        var publisher = new FakePublisher { StopAfterCheckpoint = 1, Stop = cancellation.Cancel };
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        var job = await Submit(admin, words: 620);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.ProcessNextPublicationAsync(cancellation.Token));
        using (var scope = factory.Services.CreateScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().ManualPublicationJobs.SingleAsync();
            Assert.Equal("Processing", saved.Status);
            Assert.Equal(1, saved.CompletedChunks);
            Assert.True(saved.TotalChunks > saved.CompletedChunks);
            Assert.Null(saved.CompletedAt);
            Assert.NotNull(saved.ChunksJson);
        }
        publisher.StopAfterCheckpoint = null;
        Assert.True(await factory.ProcessNextPublicationAsync());
        Assert.Equal("Succeeded", (await Status(admin, job.Id)).Status);
        Assert.Equal(new[] { 0, 1 }, publisher.StartingCheckpoints);
        Assert.All(publisher.JobIds, id => Assert.Equal(job.Id, id));
    }

    [Fact]
    public async Task AllChunksStagedStillIsNotSucceededUntilPublicationFinishes()
    {
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new FakePublisher { BeforeFinish = async token =>
        {
            staged.TrySetResult();
            await finish.Task.WaitAsync(token);
        }};
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        var job = await Submit(admin);
        var process = factory.ProcessNextPublicationAsync();
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var status = await Status(admin, job.Id);
            Assert.Equal(status.TotalChunks, status.CompletedChunks);
            Assert.Equal("Processing", status.Status);
            Assert.Null(status.CompletedAt);
            Assert.Null(status.BikeId);
        }
        finally { finish.TrySetResult(); }
        await process;
        Assert.Equal("Succeeded", (await Status(admin, job.Id)).Status);
    }

    [Fact]
    public async Task QueuedJobRechecksCatalogMappingBeforeUsingTheManual()
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        var job = await Submit(admin);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
            db.MasterBikes.Add(new MasterBike { Make = job.Make, Model = job.Model, Year = job.Year, ManualKey = "changed-key" });
            await db.SaveChangesAsync();
        }
        await factory.ProcessNextPublicationAsync();
        Assert.Equal("Failed", (await Status(admin, job.Id)).Status);
        Assert.Equal(0, publisher.Calls);
    }

    [Fact]
    public async Task StatusAndListAreOwnerScopedAndRoleRevocationPreventsQueuedWork()
    {
        var publisher = new FakePublisher();
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var owner = await AdminClient(factory);
        using var other = await AdminClient(factory, Guid.NewGuid());
        var job = await Submit(owner);
        using var denied = await other.GetAsync($"/api/admin/manual-jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<List<ManualPublicationJobResponse>>("/api/admin/manual-jobs"))!);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
            (await db.Users.SingleAsync(user => user.SupabaseUserId == AuthTestTokens.UserId)).RoleId = 42;
            await db.SaveChangesAsync();
        }
        using var forbidden = await owner.GetAsync($"/api/admin/manual-jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await factory.ProcessNextPublicationAsync();
        Assert.Equal(0, publisher.Calls);
        using var verification = factory.Services.CreateScope();
        Assert.Equal("Failed", (await verification.ServiceProvider.GetRequiredService<RideFixBroDbContext>().ManualPublicationJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task HostedWorkerContinuesAfterSubmissionRequestEnds()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new FakePublisher { BeforePublish = async token =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        }};
        await using var factory = new ChatApiFactory { ManualPublisher = publisher };
        using var admin = await AdminClient(factory);
        var job = await Submit(admin);
        using var worker = new ManualPublicationWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ManualPublicationQueue>(), NullLogger<ManualPublicationWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Processing", (await Status(admin, job.Id)).Status);
            release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!(await Status(admin, job.Id)).Status.Equals("Succeeded", StringComparison.Ordinal))
                await Task.Delay(20, timeout.Token);
        }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ChunkIdsAreStablePerJobAndDifferentAcrossChunksOrReuploads()
    {
        var job = Guid.NewGuid();
        Assert.Equal(VectorDbService.ChunkId(job, 0), VectorDbService.ChunkId(job, 0));
        Assert.NotEqual(VectorDbService.ChunkId(job, 0), VectorDbService.ChunkId(job, 1));
        Assert.NotEqual(VectorDbService.ChunkId(job, 0), VectorDbService.ChunkId(Guid.NewGuid(), 0));
    }

    private static async Task<HttpClient> AdminClient(ChatApiFactory factory, Guid? subject = null)
    {
        var client = factory.Client();
        if (subject.HasValue) client.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(subject));
        using var me = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var id = (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
        (await db.Users.SingleAsync(user => user.Id == id)).RoleId = 84;
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<ManualPublicationJobResponse> Submit(HttpClient client, string key = "x440-2024", int year = 2024, int words = 0)
    {
        using var response = await client.PostAsync("/api/admin/bikes", Form(key: key, year: year, words: words));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ManualPublicationJobResponse>())!;
    }

    private static async Task<ManualPublicationJobResponse> Status(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ManualPublicationJobResponse>($"/api/admin/manual-jobs/{id}"))!;

    private static MultipartFormDataContent Form(string? omit = null, string key = "x440-2024", int year = 2024, int skip = 0, int words = 0)
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
            var content = words > 0 ? string.Join(" ", Enumerable.Repeat("manual", words)) : "Official manual text";
            var file = new ByteArrayContent(ManualUploadTests.Pdf("Cover page", content));
            file.Headers.ContentType = new("application/pdf");
            form.Add(file, "file", "manual.pdf");
        }
        return form;
    }

    private sealed class FakePublisher : IManualPublisher
    {
        public int Calls { get; private set; }
        public List<int> StartingCheckpoints { get; } = [];
        public List<Guid> JobIds { get; } = [];
        public Func<CancellationToken, Task>? BeforePublish { get; set; }
        public Func<CancellationToken, Task>? BeforeFinish { get; init; }
        public Exception? Failure { get; init; }
        public int? StopAfterCheckpoint { get; set; }
        public Action? Stop { get; init; }
        public async Task PublishAsync(ManualPublicationWork work, Func<int, CancellationToken, Task> saveCheckpoint, CancellationToken token)
        {
            Calls++;
            StartingCheckpoints.Add(work.CompletedChunks);
            JobIds.Add(work.JobId);
            if (BeforePublish is not null) await BeforePublish(token);
            if (Failure is not null) throw Failure;
            for (var count = work.CompletedChunks + 1; count <= work.Chunks.Count; count++)
            {
                await saveCheckpoint(count, token);
                if (StopAfterCheckpoint == count) Stop?.Invoke();
                token.ThrowIfCancellationRequested();
            }
            if (BeforeFinish is not null) await BeforeFinish(token);
        }
    }
}
