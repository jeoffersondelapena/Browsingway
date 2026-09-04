using Browsingway.Renderer;
using Xunit;

namespace Browsingway.Tests;

public class ReadyGateTests
{
	[Fact]
	public void StartsClosed()
	{
		Assert.False(new ReadyGate().IsOpen);
	}

	[Fact]
	public void OpensForWaitersOnce_DXAndCEFAreUp()
	{
		ReadyGate gate = new();
		Task<bool> waiter = Task.Run(gate.WaitUntilOpen);

		Assert.False(waiter.Wait(100));   // a message arriving early must not be handled yet
		gate.Open();

		Assert.True(waiter.Wait(2000));
		Assert.True(waiter.Result);
		Assert.True(gate.IsOpen);
	}

	[Fact]
	public void ReleasesWaitersOnShutdownWithoutLettingThemRun()
	{
		// A renderer that never finishes starting must not leave IPC threads blocked forever.
		ReadyGate gate = new();
		Task<bool> waiter = Task.Run(gate.WaitUntilOpen);

		gate.Abandon();

		Assert.True(waiter.Wait(2000));
		Assert.False(waiter.Result);
		Assert.False(gate.IsOpen);
	}

	[Fact]
	public void AnAlreadyOpenGateDoesNotBlock()
	{
		ReadyGate gate = new();
		gate.Open();
		Assert.True(gate.WaitUntilOpen());
	}

	[Fact]
	public void ReleasesEveryWaiter()
	{
		ReadyGate gate = new();
		Task<bool>[] waiters = Enumerable.Range(0, 8).Select(_ => Task.Run(gate.WaitUntilOpen)).ToArray();

		gate.Open();

		Assert.True(Task.WaitAll(waiters, 2000));
		Assert.All(waiters, w => Assert.True(w.Result));
	}
}
