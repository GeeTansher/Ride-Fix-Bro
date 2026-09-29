using AutoGen.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RideFixBro.API.Configuration;
using RideFixBro.API.DataStore;
using RideFixBro.API.Models;
using RideFixBro.API.Services;
using RideFixBro.Data.Entities;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Message = RideFixBro.Data.Entities.Message;

namespace RideFixBro.API.Tests;

public class PersistentChatTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZeroCreatesAChatAndReplyReturnsTheNumericIdForFollowUps(bool general)
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        int? bikeId = null;
        if (!general)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
            db.MasterBikes.Add(new MasterBike { Id = 101, Make = "Harley-Davidson", Model = "X440", Year = 2024 });
            await db.SaveChangesAsync();
            using var added = await client.PostAsJsonAsync("/api/garage", new { bikeId = 101 });
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            bikeId = (await added.Content.ReadFromJsonAsync<GarageBikeResponse>())!.Id;
        }
        using var first = await client.PostAsJsonAsync("/api/Chat/ask",
            new { sessionId = 0, message = "First question", isGeneral = general, userBikeId = bikeId });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Number, firstBody.GetProperty("sessionId").ValueKind);
        var id = firstBody.GetProperty("sessionId").GetInt32();
        Assert.True(id > 0);
        using var next = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = id, message = "Follow-up" });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(id, (await next.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetInt32());
        using var verification = factory.Services.CreateScope();
        var row = await verification.ServiceProvider.GetRequiredService<RideFixBroDbContext>().ChatSessions.SingleAsync();
        Assert.Equal(id, row.Id);
        Assert.Equal(bikeId, row.UserBikeId);
        Assert.Equal(4, factory.History(id).Count);
    }

    [Theory]
    [InlineData("model", HttpStatusCode.InternalServerError)]
    [InlineData("tool-budget", HttpStatusCode.UnprocessableEntity)]
    [InlineData("database", HttpStatusCode.ServiceUnavailable)]
    public async Task FirstReplyFailureStillReturnsTheCreatedChatId(string failure, HttpStatusCode expected)
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        factory.Agent.Reply = _ => throw failure switch
        {
            "tool-budget" => new ChatLimitExceededException("Test budget exhausted."),
            "database" => new DbUpdateException("Test storage error."),
            _ => new InvalidOperationException("Private provider error.")
        };
        using var failed = await client.PostAsJsonAsync("/api/Chat/ask",
            new { sessionId = 0, isGeneral = true, message = "First attempt" });
        Assert.Equal(expected, failed.StatusCode);
        var id = (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetInt32();
        Assert.True(id > 0);
        Assert.Empty(factory.History(id));
        factory.Agent.Reply = _ => Task.FromResult<IMessage>(new TextMessage(Role.Assistant, "Recovered"));
        using var retried = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = id, message = "Retry" });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(id, (await retried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetInt32());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().ChatSessions.CountAsync());
        Assert.Equal(2, factory.History(id).Count);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"42\"")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("null")]
    public async Task InvalidNumericIdDoesNotCreateAChatOrCallTheAgent(string value)
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        using var content = new StringContent(
            $$"""{"sessionId":{{value}},"message":"Hi","isGeneral":true}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/Chat/ask", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Agent.Calls);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().ChatSessions.ToListAsync());
    }

    [Theory]
    [InlineData("missing-selection", HttpStatusCode.BadRequest)]
    [InlineData("both-selections", HttpStatusCode.BadRequest)]
    [InlineData("missing-bike", HttpStatusCode.NotFound)]
    [InlineData("empty-message", HttpStatusCode.BadRequest)]
    [InlineData("oversized-message", HttpStatusCode.BadRequest)]
    [InlineData("invalid-photo", HttpStatusCode.BadRequest)]
    [InlineData("unknown-chat", HttpStatusCode.NotFound)]
    public async Task InvalidFirstMessageOrSelectionDoesNotLeaveAnEmptyChat(string invalid, HttpStatusCode expected)
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/Chat/ask", new
        {
            sessionId = invalid == "unknown-chat" ? 999 : 0,
            isGeneral = invalid is not ("missing-selection" or "missing-bike"),
            userBikeId = invalid is "missing-bike" or "both-selections" ? (int?)999 : null,
            message = invalid == "empty-message" ? "" : invalid == "oversized-message" ? new string('x', 2001) : "Hi",
            imageData = invalid == "invalid-photo" ? "invalid" : null
        });
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, factory.Agent.Calls);
        using var scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().ChatSessions.ToListAsync());
    }

    [Fact]
    public async Task NewScopeRestoresDisplayHistoryAndGeneralSelectionWithoutMemory()
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        var id = await factory.CreateChatAsync(client);
        using var response = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = id, message = "Saved question" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await client.GetFromJsonAsync<ChatDetail>($"/api/chats/{id}");
        Assert.NotNull(detail);
        Assert.True(detail.Chat.IsGeneral);
        Assert.Null(detail.Chat.Bike);
        Assert.Equal("Saved question", detail.Messages[0].Text);
        Assert.Equal(2, detail.Messages.Count);
        var list = await client.GetFromJsonAsync<List<ChatSummary>>("/api/chats");
        Assert.Equal(id, Assert.Single(list!).Id);
        Assert.Equal("Saved question", list![0].Title);

        using var other = factory.Client();
        other.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(Guid.NewGuid()));
        using var denied = await other.GetAsync($"/api/chats/{id}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<List<ChatSummary>>("/api/chats"))!);
    }

    [Fact]
    public async Task SerializedGeminiCallsRestoreTheirSignaturesOnTheNextTurn()
    {
        using var handler = new RecordingHandler(
            ToolFlowTests.ToolReply(ToolFlowTests.Call("web", "SearchInternetAsync", """{"query":"gear"}""", "keep-this-signature")),
            ToolFlowTests.TextReply("First answer"), ToolFlowTests.TextReply("Restored answer"));
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        var id = await factory.CreateChatAsync(client);
        var agent = ToolFlowTests.CreateAgent(handler);
        for (var turn = 1; turn <= 2; turn++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
            var limits = new ChatLimitsOptions();
            var manager = new AiManagerService(agent, new SqlChatHistoryStore(db), limits,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AiManagerService>.Instance);
            await manager.AskMechanicBro(new ChatContext(id, null), new ChatInput($"Question {turn}", null));
        }
        ToolFlowTests.AssertValidSequence(handler.Requests[^1]);
        var toolRequest = handler.Requests[^1].GetProperty("messages").EnumerateArray()
            .First(message => message.TryGetProperty("tool_calls", out _));
        Assert.Equal("keep-this-signature", toolRequest.GetProperty("tool_calls")[0]
            .GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString());
        Assert.Equal(5, factory.History(id).Count);
    }

    [Fact]
    public async Task PhotoBytesAreNeverPersistedAndReopenDisclosesTheirAbsence()
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        var id = await factory.CreateChatAsync(client);
        using var sent = await client.PostAsJsonAsync("/api/Chat/ask",
            new { sessionId = id, message = "Look at this", imageData = ImageFixtures.JpegBase64 });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var detail = await client.GetFromJsonAsync<ChatDetail>($"/api/chats/{id}");
        Assert.True(detail!.Messages[0].PhotoNotStored);
        Assert.Equal("Look at this", detail.Messages[0].Text);
        using var scope = factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>().Messages
            .OrderBy(message => message.SequenceNumber).FirstAsync();
        Assert.DoesNotContain(ImageFixtures.JpegBase64, row.PayloadJson!);
        Assert.Contains("no longer available", Assert.IsType<TextMessage>(factory.History(id)[0]).Content);
    }

    [Fact]
    public async Task MissingSelectionUnknownChatAndChangedSelectionAreNotSilentlyAccepted()
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        using var missing = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = 0, message = "Hi" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var both = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = 0, message = "Hi", isGeneral = true, userBikeId = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        using var unknown = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = 12345, message = "Hi" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var old = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = Guid.NewGuid().ToString(), message = "Hi" });
        Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        Assert.Equal(0, factory.Agent.Calls);
    }

    [Fact]
    public async Task DatabaseKeepsAllTurnsWhileModelOnlyLoadsTheConfiguredWindow()
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        var id = await factory.CreateChatAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
        for (var turn = 1; turn <= 55; turn++)
        {
            db.Messages.AddRange(
                new Message { ChatSessionId = id, Role = "User", Content = $"Question {turn}",
                    TurnNumber = turn, SequenceNumber = turn * 2 - 1, Timestamp = DateTime.UtcNow },
                new Message { ChatSessionId = id, Role = "Assistant", Content = $"Answer {turn}",
                    TurnNumber = turn, SequenceNumber = turn * 2, Timestamp = DateTime.UtcNow });
        }
        await db.SaveChangesAsync();
        var store = new SqlChatHistoryStore(db);
        var history = await store.LoadRecentAsync(id, 2, CancellationToken.None);
        Assert.Equal(4, history.Messages.Count);
        Assert.Equal("Question 54", Assert.IsType<TextMessage>(history.Messages[0]).Content);
        await store.AppendTurnAsync(id, history,
            [new TextMessage(Role.User, "Question 56"), new TextMessage(Role.Assistant, "Answer 56")], CancellationToken.None);
        Assert.Equal(112, await db.Messages.CountAsync());
        var page = await client.GetFromJsonAsync<ChatDetail>($"/api/chats/{id}");
        Assert.Equal(100, page!.Messages.Count);
        Assert.NotNull(page.NextBeforeSequence);
        var older = await client.GetFromJsonAsync<ChatDetail>($"/api/chats/{id}?beforeSequence={page.NextBeforeSequence}");
        Assert.Equal(12, older!.Messages.Count);
        Assert.Equal("Question 1", older.Messages[0].Text);
        Assert.Null(older.NextBeforeSequence);
    }

    [Fact]
    public async Task Unmapped2026BikeCannotAdvertiseThe2024Manual()
    {
        using var handler = new RecordingHandler(ToolFlowTests.TextReply("No manual available"));
        var bike = new BikeContext(9, "Harley-Davidson", "X440", 2026, null);
        await ToolFlowTests.CreateManager(ToolFlowTests.CreateAgent(handler), new InMemoryChatStore())
            .AskMechanicBro(new ChatContext(1, bike), new ChatInput("Question", null));
        Assert.DoesNotContain(handler.Requests[0].GetProperty("tools").EnumerateArray(), tool =>
            tool.GetProperty("function").GetProperty("name").GetString() == "SearchManualAsync");
    }

    [Fact]
    public async Task RecentlyUpdatedOldChatComesFirstAndPaginationIsOwnerScoped()
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        var first = await factory.CreateChatAsync(client);
        var second = await factory.CreateChatAsync(client);
        using var updated = await client.PostAsJsonAsync("/api/Chat/ask", new { sessionId = first, message = "Latest activity" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var list = await client.GetFromJsonAsync<List<ChatSummary>>("/api/chats");
        Assert.Equal(new[] { first, second }, list!.Select(chat => chat.Id));
        var next = await client.GetFromJsonAsync<List<ChatSummary>>($"/api/chats?beforeId={first}");
        Assert.Equal(second, Assert.Single(next!).Id);
        using var other = factory.Client();
        other.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(Guid.NewGuid()));
        using var deniedCursor = await other.GetAsync($"/api/chats?beforeId={first}");
        Assert.Equal(HttpStatusCode.NotFound, deniedCursor.StatusCode);
    }

    [Fact]
    public async Task TwoIndependentWritersCannotCommitInterleavedOrPartialTurns()
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        var id = await factory.CreateChatAsync(client);
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
        var first = new SqlChatHistoryStore(firstDb);
        var second = new SqlChatHistoryStore(secondDb);
        var stale = await first.LoadRecentAsync(id, 10, CancellationToken.None);
        var fresh = await second.LoadRecentAsync(id, 10, CancellationToken.None);
        await second.AppendTurnAsync(id, fresh,
            [new TextMessage(Role.User, "saved user"), new TextMessage(Role.Assistant, "saved answer")], CancellationToken.None);
        // SQLite enforces the same unique sequence invariant; SQL Server additionally maps this to HTTP 409.
        await Assert.ThrowsAsync<DbUpdateException>(() => first.AppendTurnAsync(id, stale,
            [new TextMessage(Role.User, "stale user"), new TextMessage(Role.Assistant, "stale answer")], CancellationToken.None));
        var saved = factory.History(id);
        Assert.Equal(2, saved.Count);
        Assert.Equal("saved user", Assert.IsType<TextMessage>(saved[0]).Content);
        Assert.Equal("saved answer", Assert.IsType<TextMessage>(saved[1]).Content);
    }

    [Fact]
    public async Task SeparateCreateEndpointIsNoLongerExposed()
    {
        await using var factory = new ChatApiFactory();
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/chats", new { isGeneral = true });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(0, factory.Agent.Calls);
    }

    [Fact]
    public async Task OwnedContextCarriesManualKeyWithoutLeakingItIntoResponses()
    {
        await using var factory = new ChatApiFactory();
        using var owner = factory.Client();
        using var profile = await owner.GetAsync("/api/me");
        var userId = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RideFixBroDbContext>();
            db.MasterBikes.Add(new MasterBike { Id = 101, Make = "Harley-Davidson", Model = "X440", Year = 2024, ManualKey = "catalog-key" });
            await db.SaveChangesAsync();
        }
        using var added = await owner.PostAsJsonAsync("/api/garage", new { bikeId = 101 });
        var garage = await added.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(garage.TryGetProperty("manualKey", out _));
        var garageId = garage.GetProperty("id").GetInt32();
        ChatContext context;
        using (var scope = factory.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ChatSessionService>();
            context = await sessions.CreateAsync(userId, garageId, false, CancellationToken.None);
            Assert.Equal("catalog-key", context.Bike!.ManualKey);
            Assert.Equal(garageId, context.Bike.UserBikeId);
            Assert.Equal(context, await sessions.GetContextAsync(userId, context.Id, CancellationToken.None));
        }
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/chats/{context.Id}");
        Assert.False(detail.GetProperty("chat").GetProperty("bike").TryGetProperty("manualKey", out _));
        using var other = factory.Client();
        other.DefaultRequestHeaders.Authorization = new("Bearer", AuthTestTokens.Create(Guid.NewGuid()));
        using var denied = await other.PostAsJsonAsync("/api/Chat/ask", new { sessionId = context.Id, message = "Not mine" });
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(0, factory.Agent.Calls);
    }
}
