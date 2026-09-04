using System.Diagnostics;

namespace Browsingway;

// Dalamud's log is a single file that only one game instance wins, so a second instance leaves no
// trace at all.
internal static class DiagLog
{
	private const int _keepFiles = 12;
	private static readonly object _lock = new();
	private static string? _path;

	public static void Open(string configDir, int gamePid)
	{
		try
		{
			string dir = Path.Combine(configDir, "logs");
			Directory.CreateDirectory(dir);

			foreach (string old in Directory.EnumerateFiles(dir, "bw-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Skip(_keepFiles - 1))
			{
				try { File.Delete(old); }
				catch (Exception) { }
			}

			_path = Path.Combine(dir, $"bw-{DateTime.Now:yyyyMMdd-HHmmss}-{gamePid}.log");
			Write($"game pid {gamePid}, plugin {typeof(DiagLog).Assembly.GetName().Version}");
		}
		catch (Exception e)
		{
			_path = null;
			Services.PluginLog.Warning($"Could not open the Browsingway diagnostic log: {e.Message}");
		}
	}

	public static void Write(string message)
	{
		if (_path is null)
		{
			return;
		}

		try
		{
			lock (_lock)
			{
				File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
			}
		}
		catch (Exception) { }
	}

	public static void Write(string message, Process process)
	{
		int pid;
		try { pid = process.Id; }
		catch (Exception) { pid = -1; }

		Write($"{message} (renderer pid {pid})");
	}
}
