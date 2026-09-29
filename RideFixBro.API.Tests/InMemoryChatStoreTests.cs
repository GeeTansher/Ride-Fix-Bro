using AutoGen.Core;
using RideFixBro.API.Services;

namespace RideFixBro.API.Tests;

public class InMemoryChatStoreTests
{
    [Fact]
    public async Task StaleAppendIsRejectedWithoutAffectingAnotherChat()
    {
        var store = new InMemoryChatStore();
        var first = await store.LoadRecentAsync(1, 10, CancellationToken.None);
        var stale = await store.LoadRecentAsync(1, 10, CancellationToken.None);
        await store.AppendTurnAsync(1, first, [new TextMessage(Role.User, "first")], CancellationToken.None);
        var error = await Assert.ThrowsAsync<ChatInputException>(() =>
            store.AppendTurnAsync(1, stale, [new TextMessage(Role.User, "stale")], CancellationToken.None));
        Assert.Equal(409, error.StatusCode);
        var other = await store.LoadRecentAsync(2, 10, CancellationToken.None);
        await store.AppendTurnAsync(2, other, [new TextMessage(Role.User, "other")], CancellationToken.None);
        Assert.Equal("first", Assert.IsType<TextMessage>(Assert.Single(store.GetHistory(1))).Content);
        Assert.Equal("other", Assert.IsType<TextMessage>(Assert.Single(store.GetHistory(2))).Content);
    }

    [Fact]
    public async Task ContextWindowsAndExternalListsDoNotDeleteSavedTurns()
    {
        var store = new InMemoryChatStore();
        var turn = new List<IMessage> { new TextMessage(Role.User, "first") };
        await store.AppendTurnAsync(1, await store.LoadRecentAsync(1, 1, CancellationToken.None), turn, CancellationToken.None);
        turn.Clear();
        store.GetHistory(1).Clear();
        await store.AppendTurnAsync(1, await store.LoadRecentAsync(1, 1, CancellationToken.None),
            [new TextMessage(Role.User, "second")], CancellationToken.None);
        var latest = await store.LoadRecentAsync(1, 1, CancellationToken.None);
        Assert.Equal("second", Assert.IsType<TextMessage>(Assert.Single(latest.Messages)).Content);
        Assert.Equal(2, store.GetHistory(1).Count);
    }

    [Fact]
    public async Task CancelledAppendDoesNotSaveAnyMessages()
    {
        var store = new InMemoryChatStore();
        var history = await store.LoadRecentAsync(1, 10, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AppendTurnAsync(1, history,
            [new TextMessage(Role.User, "cancelled")], cancellation.Token));
        Assert.Empty(store.GetHistory(1));
    }
}
