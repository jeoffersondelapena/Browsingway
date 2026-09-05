namespace Browsingway;

public static class RendererStartPolicy
{
	// Spawning inside the boot-time plugin-load burst has hung the game under Wine; later there is no burst.
	public const int BootDelayMs = 20000;
	public static readonly TimeSpan BootWindow = TimeSpan.FromMinutes(3);

	public static int DelayMs(TimeSpan gameAge) => gameAge < BootWindow ? BootDelayMs : 0;
}
