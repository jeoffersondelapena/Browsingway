namespace Browsingway;

internal static class CacheSlotPolicy
{
	public const string RendererProcessName = "Browsingway.Renderer";

	public static bool TryParseOwner(string? text, out int gamePid, out int rendererPid)
	{
		gamePid = rendererPid = 0;
		if (text is null) { return false; }

		string[] parts = text.Trim().Split(' ');
		return parts.Length == 2
			&& int.TryParse(parts[0], out gamePid)
			&& int.TryParse(parts[1], out rendererPid);
	}

	public static string FormatOwner(int gamePid, int rendererPid) => $"{gamePid} {rendererPid}";

	// Wine hands the game the same pid every launch, so a record naming ours predates us.
	public static bool RecordIsAbandoned(int recordedGamePid, int ourPid, bool recordedGameAlive)
		=> recordedGamePid == ourPid || !recordedGameAlive;

	// Renderer pids are reused too, so the name has to match before we kill.
	public static bool ShouldKillRenderer(bool alive, string? processName)
		=> alive && string.Equals(processName, RendererProcessName, StringComparison.OrdinalIgnoreCase);

	public static string SlotName(string baseName, int slot)
		=> slot == 1 ? baseName : $"{baseName}-{slot}";

	// Slots and ports are both handed out lowest-free in launch order; the port helper uses the same rule.
	public static int PortForSlot(int slot) => 10500 + slot;

	public static string RepointLocalhost(string url, int port)
		=> System.Text.RegularExpressions.Regex.Replace(url, @"(127\.0\.0\.1:)1050\d", $"${{1}}{port}");
}
