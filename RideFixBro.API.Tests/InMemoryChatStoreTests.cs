using AutoGen.Core;
using RideFixBro.API.DataStore;

namespace RideFixBro.API.Tests
{
	public class InMemoryChatStoreTests
	{
		[Fact]
		public async Task UpdatesAreSerializedWithinASessionButNotAcrossSessions()
		{
			var store = new InMemoryChatStore();
			var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var secondStarted = false;
			var first = store.UpdateHistoryAsync("same", async history =>
			{
				firstStarted.SetResult();
				await releaseFirst.Task;
				history.Add(new TextMessage(Role.User, "first"));
				return 1;
			});
			await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var second = store.UpdateHistoryAsync("same", history =>
			{
				secondStarted = true;
				Assert.Equal("first", Assert.IsType<TextMessage>(Assert.Single(history)).Content);
				history.Add(new TextMessage(Role.User, "second"));
				return Task.FromResult(2);
			});

			try
			{
				Assert.False(secondStarted);
				Assert.Equal(3, await store.UpdateHistoryAsync("other", _ => Task.FromResult(3))
					.WaitAsync(TimeSpan.FromSeconds(5)));
			}
			finally
			{
				releaseFirst.TrySetResult();
			}

			await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(2, store.GetHistory("same").Count);
		}

		[Fact]
		public async Task FailedUpdatesAndExternalListMutationsDoNotChangeSavedHistory()
		{
			var store = new InMemoryChatStore();
			List<IMessage>? workingHistory = null;
			await store.UpdateHistoryAsync("session", history =>
			{
				workingHistory = history;
				history.Add(new TextMessage(Role.User, "saved"));
				return Task.FromResult(1);
			});
			workingHistory!.Clear();
			store.GetHistory("session").Clear();
			await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateHistoryAsync<int>("session", history =>
			{
				history.Add(new TextMessage(Role.User, "failed"));
				throw new InvalidOperationException("failure");
			}));

			Assert.Equal("saved", Assert.IsType<TextMessage>(Assert.Single(store.GetHistory("session"))).Content);
		}

		[Fact]
		public async Task CancellationRollsBackAndReleasesTheSessionLock()
		{
			var store = new InMemoryChatStore();
			using var cancellation = new CancellationTokenSource();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				store.UpdateHistoryAsync("session", history =>
				{
					history.Add(new TextMessage(Role.User, "cancelled"));
					cancellation.Cancel();
					return Task.FromResult(1);
				}, cancellation.Token));

			Assert.Empty(store.GetHistory("session"));
			Assert.Equal(2, await store.UpdateHistoryAsync("session", _ => Task.FromResult(2))
				.WaitAsync(TimeSpan.FromSeconds(5)));
		}
	}
}
