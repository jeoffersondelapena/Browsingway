using Browsingway;
using Xunit;

public class TextureWaitTests
{
	[Fact]
	public void An_overlay_that_is_not_waiting_never_asks_again()
	{
		Assert.False(TextureWait.ShouldAskAgain(waiting: false, waitedMs: 600000, asks: 1));
	}

	[Fact]
	public void A_page_gets_its_usual_few_seconds_before_the_overlay_asks_again()
	{
		Assert.False(TextureWait.ShouldAskAgain(true, 4000, 1));
		Assert.False(TextureWait.ShouldAskAgain(true, TextureWait.AskAgainAfterMs - 1, 1));
		Assert.True(TextureWait.ShouldAskAgain(true, TextureWait.AskAgainAfterMs, 1));
	}

	[Fact]
	public void Asking_stops_after_a_few_tries_instead_of_reloading_the_page_forever()
	{
		Assert.True(TextureWait.ShouldAskAgain(true, 60000, TextureWait.MaxAsks - 1));
		Assert.False(TextureWait.ShouldAskAgain(true, 60000, TextureWait.MaxAsks));
	}

	[Fact]
	public void A_hidden_overlay_without_a_picture_is_not_a_fault()
	{
		Assert.False(TextureWait.Overdue(shown: false, blankMs: 600000));
	}

	[Fact]
	public void After_the_renderer_was_lost_a_shown_overlay_is_blank_until_its_texture_is_back()
	{
		Assert.True(TextureWait.Blank(shown: true, rendererLost: true, blankMs: 0));
		Assert.False(TextureWait.Blank(shown: false, rendererLost: true, blankMs: 600000));
		Assert.False(TextureWait.Blank(shown: true, rendererLost: false, blankMs: 3500));
		Assert.True(TextureWait.Blank(shown: true, rendererLost: false, blankMs: TextureWait.AskAgainAfterMs));
	}

	[Fact]
	public void A_shown_overlay_is_overdue_only_after_the_wait_a_page_normally_needs()
	{
		Assert.False(TextureWait.Overdue(true, 0));
		Assert.False(TextureWait.Overdue(true, 3500));
		Assert.True(TextureWait.Overdue(true, TextureWait.AskAgainAfterMs));
	}
}
