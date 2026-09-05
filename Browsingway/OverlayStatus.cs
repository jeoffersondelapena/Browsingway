namespace Browsingway;

public static class OverlayStatus
{
	public static bool Healthy(bool running, bool ready) => running && ready;

	public static string Describe(bool running, bool ready, int port, uint restarts)
	{
		string state = !running ? "renderer stopped" : ready ? "overlays ready" : "renderer starting";
		string tail = restarts == 0 ? "" : $", {restarts} crash restart(s)";
		return $"{state} on port {port}{tail}";
	}
}
