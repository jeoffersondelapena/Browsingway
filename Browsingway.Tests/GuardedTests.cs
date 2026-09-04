using Browsingway;
using Xunit;

namespace Browsingway.Tests;

file sealed class Channel
{
	public bool Disposed;
	public void Dispose() => Disposed = true;
}

public class GuardedTests
{
	[Fact]
	public void DropsTheCallWhenThereIsNoValue()
	{
		Guarded<Channel> guarded = new();
		Assert.False(guarded.HasValue);
		Assert.False(guarded.Use(_ => Assert.Fail("must not run without a value")));
	}

	[Fact]
	public void ReplaceDisposesTheOldValue()
	{
		Guarded<Channel> guarded = new();
		Channel? first = null;
		guarded.Replace(() => first = new Channel(), c => c.Dispose());
		guarded.Replace(() => new Channel(), c => c.Dispose());

		Assert.True(first!.Disposed);
	}

	[Fact]
	public void ClearDisposesAndLeavesNothingBehind()
	{
		Guarded<Channel> guarded = new();
		Channel? only = null;
		guarded.Replace(() => only = new Channel(), c => c.Dispose());

		guarded.Clear(c => c.Dispose());

		Assert.True(only!.Disposed);
		Assert.False(guarded.HasValue);
		Assert.False(guarded.Use(_ => Assert.Fail("must not run after Clear")));
	}

	[Fact]
	public void ClearIsIdempotent()
	{
		Guarded<Channel> guarded = new();
		guarded.Replace(() => new Channel(), c => c.Dispose());
		guarded.Clear(c => c.Dispose());

		int disposals = 0;
		guarded.Clear(_ => disposals++);
		Assert.Equal(0, disposals);
	}

	[Fact]
	public void AUserNeverSeesADisposedChannelWhileItIsBeingReplaced()
	{
		// The real failure: the draw thread sends while a background task restarts the renderer.
		Guarded<Channel> guarded = new();
		guarded.Replace(() => new Channel(), c => c.Dispose());

		List<string> violations = new();
		using CancellationTokenSource stop = new();

		Task replacing = Task.Run(() =>
		{
			while (!stop.IsCancellationRequested)
			{
				guarded.Replace(() => new Channel(), c => c.Dispose());
			}
		});

		Task[] senders = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
		{
			while (!stop.IsCancellationRequested)
			{
				guarded.Use(c =>
				{
					if (c.Disposed)
					{
						lock (violations) { violations.Add("used a disposed channel"); }
					}
				});
			}
		})).ToArray();

		Thread.Sleep(750);
		stop.Cancel();
		Task.WaitAll(senders.Append(replacing).ToArray(), 5000);

		Assert.Empty(violations);
	}
}
