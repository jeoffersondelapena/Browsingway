using Browsingway;
using Xunit;

public class OverlayStatusTests
{
	[Fact]
	public void Healthy_means_running_and_ready()
	{
		Assert.True(OverlayStatus.Healthy(running: true, ready: true));
		Assert.False(OverlayStatus.Healthy(running: true, ready: false));
		Assert.False(OverlayStatus.Healthy(running: false, ready: true));
	}

	[Fact]
	public void The_line_says_which_state_and_which_port()
	{
		Assert.Equal("overlays ready on port 10501", OverlayStatus.Describe(true, true, 10501, 0));
		Assert.Equal("renderer starting on port 10502", OverlayStatus.Describe(true, false, 10502, 0));
		Assert.Equal("renderer stopped on port 10501", OverlayStatus.Describe(false, false, 10501, 0));
	}

	[Fact]
	public void An_overlay_on_screen_without_a_picture_is_not_healthy_and_is_named()
	{
		Assert.True(OverlayStatus.Healthy(true, true, Array.Empty<string>()));
		Assert.False(OverlayStatus.Healthy(true, true, new[] { "DPS" }));
		Assert.Equal("overlays ready on port 10501; no picture for DPS", OverlayStatus.Describe(true, true, 10501, 0, new[] { "DPS" }));
		Assert.Equal("overlays ready on port 10501, 1 crash restart(s); no picture for DPS, Cactbot", OverlayStatus.Describe(true, true, 10501, 1, new[] { "DPS", "Cactbot" }));
		Assert.Equal("overlays ready on port 10501", OverlayStatus.Describe(true, true, 10501, 0, Array.Empty<string>()));
	}

	[Fact]
	public void Crash_restarts_are_mentioned_only_when_there_were_any()
	{
		Assert.Equal("overlays ready on port 10501, 2 crash restart(s)", OverlayStatus.Describe(true, true, 10501, 2));
	}
}
