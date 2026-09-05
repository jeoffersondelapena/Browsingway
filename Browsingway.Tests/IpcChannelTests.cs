using Browsingway;
using Xunit;

public class IpcChannelTests
{
	[Fact]
	public void Every_channel_gets_a_name_no_earlier_renderer_could_have_used()
	{
		string first = IpcChannel.Fresh("BrowsingwayRendererIpcChannel364");
		string second = IpcChannel.Fresh("BrowsingwayRendererIpcChannel364");
		Assert.NotEqual(first, second);
		Assert.StartsWith("BrowsingwayRendererIpcChannel364_", first);
	}

	[Fact]
	public void Names_stay_safe_for_named_kernel_objects()
	{
		string name = IpcChannel.Fresh("BrowsingwayRendererIpcChannel364");
		Assert.DoesNotContain("\\", name);
		Assert.DoesNotContain("-", name);
		Assert.True(name.Length < 80);
	}
}
