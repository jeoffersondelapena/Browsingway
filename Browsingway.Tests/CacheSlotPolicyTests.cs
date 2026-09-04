using Browsingway;
using Xunit;

namespace Browsingway.Tests;

public class OwnerRecordTests
{
	[Theory]
	[InlineData("364 2672", 364, 2672)]
	[InlineData("  364 2672  ", 364, 2672)]
	public void ParsesAWellFormedRecord(string text, int game, int renderer)
	{
		Assert.True(CacheSlotPolicy.TryParseOwner(text, out int gamePid, out int rendererPid));
		Assert.Equal(game, gamePid);
		Assert.Equal(renderer, rendererPid);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("364")]
	[InlineData("364 2672 99")]
	[InlineData("364 renderer")]
	[InlineData("not a record")]
	public void RejectsAnythingElse(string? text)
	{
		Assert.False(CacheSlotPolicy.TryParseOwner(text, out _, out _));
	}

	[Fact]
	public void RoundTrips()
	{
		Assert.True(CacheSlotPolicy.TryParseOwner(CacheSlotPolicy.FormatOwner(364, 2672), out int g, out int r));
		Assert.Equal((364, 2672), (g, r));
	}
}

public class AbandonedRecordTests
{
	[Fact]
	public void ARecordNamingOurOwnPidIsFromAPreviousLaunch()
	{
		// Wine gives the game the same pid every launch, so "alive" tells us nothing here.
		Assert.True(CacheSlotPolicy.RecordIsAbandoned(recordedGamePid: 364, ourPid: 364, recordedGameAlive: true));
	}

	[Fact]
	public void ARecordNamingADeadGameIsAbandoned()
	{
		Assert.True(CacheSlotPolicy.RecordIsAbandoned(recordedGamePid: 2312, ourPid: 364, recordedGameAlive: false));
	}

	[Fact]
	public void ARecordNamingAnotherLiveWindowIsLeftAlone()
	{
		// The multibox case: window 2 is running and must keep its slot while window 1 relaunches.
		Assert.False(CacheSlotPolicy.RecordIsAbandoned(recordedGamePid: 2312, ourPid: 364, recordedGameAlive: true));
	}
}

public class KillGuardTests
{
	[Fact]
	public void KillsARendererThatIsStillRunning()
	{
		Assert.True(CacheSlotPolicy.ShouldKillRenderer(alive: true, processName: "Browsingway.Renderer"));
	}

	[Fact]
	public void DoesNotKillAReusedPidBelongingToSomethingElse()
	{
		// Regression: Wine reuses renderer pids too. Killing on pid alone can kill an unrelated process.
		Assert.False(CacheSlotPolicy.ShouldKillRenderer(alive: true, processName: "ffxiv_dx11"));
		Assert.False(CacheSlotPolicy.ShouldKillRenderer(alive: true, processName: "CefSharp.BrowserSubprocess"));
	}

	[Fact]
	public void DoesNotKillWhenTheNameIsUnknown()
	{
		Assert.False(CacheSlotPolicy.ShouldKillRenderer(alive: true, processName: null));
	}

	[Fact]
	public void DoesNothingForAPidThatIsAlreadyGone()
	{
		Assert.False(CacheSlotPolicy.ShouldKillRenderer(alive: false, processName: "Browsingway.Renderer"));
	}
}

public class SlotNameTests
{
	[Fact]
	public void FirstSlotKeepsTheLegacyName()
	{
		// Renaming it would orphan every existing profile.
		Assert.Equal("cef-cache", CacheSlotPolicy.SlotName("cef-cache", 1));
	}

	[Theory]
	[InlineData(2, "cef-cache-2")]
	[InlineData(16, "cef-cache-16")]
	public void LaterSlotsAreSuffixed(int slot, string expected)
	{
		Assert.Equal(expected, CacheSlotPolicy.SlotName("cef-cache", slot));
	}
}
