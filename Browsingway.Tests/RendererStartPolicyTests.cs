using Browsingway;
using Xunit;

public class RendererStartPolicyTests
{
	[Fact]
	public void A_freshly_booted_game_waits_out_the_plugin_load_burst()
	{
		Assert.Equal(RendererStartPolicy.BootDelayMs, RendererStartPolicy.DelayMs(TimeSpan.FromSeconds(30)));
		Assert.Equal(RendererStartPolicy.BootDelayMs, RendererStartPolicy.DelayMs(TimeSpan.Zero));
	}

	[Fact]
	public void A_game_that_has_been_up_for_a_while_starts_the_renderer_at_once()
	{
		Assert.Equal(0, RendererStartPolicy.DelayMs(TimeSpan.FromMinutes(3)));
		Assert.Equal(0, RendererStartPolicy.DelayMs(TimeSpan.FromHours(2)));
	}
}
