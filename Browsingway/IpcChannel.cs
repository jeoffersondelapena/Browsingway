namespace Browsingway;

public static class IpcChannel
{
	// Stale shared-memory objects outlive a killed renderer; a reused name dies with "file already exists".
	public static string Fresh(string baseName) => $"{baseName}_{Guid.NewGuid():N}";
}
