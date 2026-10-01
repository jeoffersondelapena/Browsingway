namespace Browsingway;

// Limits for an overlay whose texture has not arrived: when to ask the renderer again, when to report it.
public static class TextureWait
{
	public const int AskAgainAfterMs = 10000;
	public const int MaxAsks = 3;

	public static bool ShouldAskAgain(bool waiting, long waitedMs, int asks) => waiting && waitedMs >= AskAgainAfterMs && asks < MaxAsks;

	public static bool Overdue(bool shown, long blankMs) => shown && blankMs >= AskAgainAfterMs;
}
