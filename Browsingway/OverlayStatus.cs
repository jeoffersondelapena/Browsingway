namespace Browsingway;

public static class OverlayStatus
{
	// blank: overlays shown with no texture; the renderer can be running and ready while one has none
	public static bool Healthy(bool running, bool ready, IReadOnlyCollection<string>? blank = null) => running && ready && (blank is null || blank.Count == 0);

	public static string Describe(bool running, bool ready, int port, uint restarts, IReadOnlyCollection<string>? blank = null)
	{
		string state = !running ? "renderer stopped" : ready ? "overlays ready" : "renderer starting";
		string tail = restarts == 0 ? "" : $", {restarts} crash restart(s)";
		string missing = blank is null || blank.Count == 0 ? "" : $"; no picture for {string.Join(", ", blank)}";
		return $"{state} on port {port}{tail}{missing}";
	}
}
