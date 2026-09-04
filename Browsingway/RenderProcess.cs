using Browsingway.Common;
using Browsingway.Common.Ipc;
using Dalamud.Plugin.Services;
using System.Diagnostics;

namespace Browsingway;

internal class RenderProcess : IDisposable
{
	public event EventHandler? Crashed;
	public event Action<SetCursorMessage>? SetCursor;
	public event Action<RendererReadyMessage>? RendererReady;
	public event Action<UpdateTextureMessage>? UpdateTexture;

	private readonly Guarded<BrowsingwayRpc> _rpc = new();

	private readonly DependencyManager _dependencyManager;

	private readonly string _ipcChannelName;

	private readonly string _keepAliveHandleName;
	private readonly int _parentPid;
	private readonly string _pluginDir;

	private readonly string _cefCacheDir;
	private readonly FileStream? _cacheSlotLock;
	private readonly int _cacheSlot;

	public int CacheSlot => _cacheSlot;
	public uint RestartCount => _restartCount;
	public bool IsRunning => _running;

	private const string _cefCacheDirName = "cef-cache";
	private const int _maxCacheSlots = 16;

	private DateTime _lastRenderCheck = DateTime.MinValue;
	private uint _restartCount = 0;

	private const uint _maxRestarts = 5;
	private const int _restartDelayMs = 5000;
	private const uint _checkDelaySeconds = 1;
	private const uint _processOkAfterSeconds = 5;

	private Process _process;
	private bool _running;

	public RenderProcess(int pid,
		string pluginDir,
		string configDir,
		DependencyManager dependencyManager,
		IPluginLog pluginLog
	)
	{
		_keepAliveHandleName = $"BrowsingwayRendererKeepAlive{pid}";
		_ipcChannelName = $"BrowsingwayRendererIpcChannel{pid}";
		_dependencyManager = dependencyManager;
		_pluginDir = pluginDir;
		_parentPid = pid;

		DiagLog.Open(configDir, pid);
		(_cefCacheDir, _cacheSlotLock, _cacheSlot) = ClaimCacheSlot(configDir);
		DiagLog.Write($"using cache slot {_cefCacheDir}, lock {(_cacheSlotLock is null ? "unheld" : "held")}");

		try
		{
			DiagLog.Write($"opening ipc channel {_ipcChannelName}");
			OpenChannel();
			DiagLog.Write("ipc channel open");
			_process = SetupProcess();
		}
		catch
		{
			_cacheSlotLock?.Dispose();
			throw;
		}
	}

	/// <summary>Sends on the current channel, or drops the call if there is none.</summary>
	public void Send(Action<BrowsingwayRpc> call) => _rpc.Use(call);

	private void OpenChannel()
	{
		_rpc.Replace(() =>
		{
			BrowsingwayRpc rpc = new(_ipcChannelName);
			// Forwarding is re-attached per channel; binding subscribers to the instance would drop
			// them on a restart, leaving a healthy renderer with no overlays.
			rpc.SetCursor += msg => SetCursor?.Invoke(msg);
			rpc.RendererReady += msg => RendererReady?.Invoke(msg);
			rpc.UpdateTexture += msg => UpdateTexture?.Invoke(msg);
			return rpc;
		}, rpc => rpc.Dispose());
	}

	private string OwnerFilePath => Path.Combine(_cefCacheDir, "owner.pid");

	private void RecordOwner()
	{
		// A renderer outliving its game keeps CEF's profile lock, blocking whoever claims that
		// slot next; recording both pids lets the next start find and remove it.
		try { File.WriteAllText(OwnerFilePath, CacheSlotPolicy.FormatOwner(_parentPid, _process.Id)); }
		catch (Exception e) { Services.PluginLog.Debug($"Could not record cache slot owner: {e.Message}"); }
	}

	private void ClearOwner()
	{
		try { File.Delete(OwnerFilePath); }
		catch (Exception) { }
	}

	private void SweepAbandonedRenderers(string configDir)
	{
		foreach (string dir in Directory.EnumerateDirectories(configDir, $"{_cefCacheDirName}*"))
		{
			string owner = Path.Combine(dir, "owner.pid");
			try
			{
				if (!CacheSlotPolicy.TryParseOwner(File.ReadAllText(owner), out int gamePid, out int rendererPid))
				{
					continue;
				}

				if (!CacheSlotPolicy.RecordIsAbandoned(gamePid, _parentPid, IsAlive(gamePid)))
				{
					continue;
				}

				Process? renderer = TryGetProcess(rendererPid);
				if (CacheSlotPolicy.ShouldKillRenderer(renderer is not null, renderer?.ProcessName))
				{
					renderer!.Kill();
					if (!renderer.WaitForExit(5000))
					{
						DiagLog.Write($"renderer {rendererPid} did not exit; leaving {dir} to the next slot");
						continue;
					}

					Services.PluginLog.Info($"Removed renderer {rendererPid} left behind by game {gamePid} in {dir}");
					DiagLog.Write($"swept renderer {rendererPid} left behind by game {gamePid} in {dir}");
				}

				File.Delete(owner);
			}
			catch (FileNotFoundException) { }
			catch (DirectoryNotFoundException) { }
			catch (Exception e) { Services.PluginLog.Warning($"Could not check {owner}: {e.Message}"); }
		}
	}

	private static bool IsAlive(int pid) => TryGetProcess(pid) is not null;

	private static Process? TryGetProcess(int pid)
	{
		try
		{
			Process process = Process.GetProcessById(pid);
			return process.HasExited ? null : process;
		}
		catch (Exception) { return null; }
	}

	private (string dir, FileStream? slotLock, int slot) ClaimCacheSlot(string configDir)
	{
		SweepAbandonedRenderers(configDir);

		// Claimed once here, not in SetupProcess, so the profile dir stays stable across renderer restarts.
		// Slot 1 keeps the original name; the OS releases the lock however the process ends.
		string firstDir = Path.Combine(configDir, _cefCacheDirName);
		IOException? lastInUse = null;

		for (int slot = 1; slot <= _maxCacheSlots; slot++)
		{
			string name = CacheSlotPolicy.SlotName(_cefCacheDirName, slot);
			string dir = Path.Combine(configDir, name);

			try
			{
				FileStream slotLock = new(Path.Combine(configDir, $"{name}.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
				if (slot > 1)
				{
					Services.PluginLog.Info($"Using CEF cache slot {slot} ({dir}); earlier slots are held by other instances");
				}

				return (dir, slotLock, slot);
			}
			catch (IOException e)
			{
				lastInUse = e;
				Services.PluginLog.Debug($"CEF cache slot {slot} could not be claimed: {e.Message}");
			}
			catch (Exception e)
			{
				// not another instance; the lock only arbitrates multiboxing, so run unlocked rather than fail
				Services.PluginLog.Error(e, $"Could not open the CEF cache lock in {configDir}; running unlocked on {firstDir}");
				return (firstDir, null, 1);
			}
		}

		Services.PluginLog.Error(lastInUse, $"All {_maxCacheSlots} CEF cache slots are in use; running unlocked on {firstDir}");
		return (firstDir, null, 1);
	}

	public void Dispose()
	{
		try
		{
			Stop();

			_process.Dispose();
			_rpc.Clear(rpc => rpc.Dispose());
		}
		finally
		{
			_cacheSlotLock?.Dispose();
		}
	}

	public void Start()
	{
		if (_running)
		{
			return;
		}

		_process.Start();
		_process.BeginOutputReadLine();
		_process.BeginErrorReadLine();
		RecordOwner();
		DiagLog.Write("renderer started", _process);

		_running = true;
	}

	private int _restarting = 0; // This needs to be a numeric type for Interlocked.Exchange

	public void EnsureRenderProcessIsAlive()
	{
		if (!_running)
		{
			return;
		}

		// only check every second, reduces stress on the render thread
		if (DateTime.Now - _lastRenderCheck < TimeSpan.FromSeconds(_checkDelaySeconds))
		{
			return;
		}

		_lastRenderCheck = DateTime.Now;

		if (!HasProcessExited())
		{
			// process is still running, reset restart counter if it ran for at least 5 seconds
			if (_restartCount > 0 && DateTime.Now - _process.StartTime > TimeSpan.FromSeconds(_processOkAfterSeconds))
			{
				_restartCount = 0;
			}

			return;
		}

		if (_restartCount >= _maxRestarts)
		{
			Services.PluginLog.Error("Render process is crashing in a loop - please check the logs. No further restarts will be attempted until Browsingway is restarted.");
			DiagLog.Write($"giving up after {_maxRestarts} restarts; no browser until the plugin is reloaded");
			Services.Chat.PrintError($"Browsingway: renderer failed {_maxRestarts} times; overlays are off until the plugin is restarted.");
			Stop();
			_rpc.Clear(rpc => rpc.Dispose());
			OnProcessCrashed();
			return;
		}

		Task.Run(() =>
		{
			if (_hasExited && 0 == Interlocked.Exchange(ref _restarting, 1))
			{
				try
				{
					// process crashed, restart
					_restartCount++;
					Services.PluginLog.Error($"Render process crashed - will restart asap (attempt {_restartCount}/{_maxRestarts}).");
					DiagLog.Write($"renderer crashed, restart {_restartCount}/{_maxRestarts}");
					// Spawning into the crashed renderer's teardown has collided with its sections and once hung the game.
					Thread.Sleep(_restartDelayMs);
					OpenChannel();
					DiagLog.Write("ipc channel rebuilt for the restart");
					_process = SetupProcess();
					_process.Start();
					_process.BeginOutputReadLine();
					_process.BeginErrorReadLine();
					RecordOwner();
					DiagLog.Write("renderer restarted", _process);

					// notify everyone that we have to reinit
					OnProcessCrashed();

					// reset the process exit flag
					_hasExited = false;
				}
				catch (Exception e)
				{
					Services.PluginLog.Error(e, "Failed to restart render process");
				}
				finally
				{
					Interlocked.Exchange(ref _restarting, 0);
				}
			}
		});
	}

	public void Stop()
	{
		if (!_running) { return; }

		_running = false;

		// Grab the handle the process is waiting on and open it up
		EventWaitHandle handle = new(false, EventResetMode.ManualReset, _keepAliveHandleName);
		handle.Set();
		handle.Dispose();

		// Give the process a sec to gracefully shut down, then kill it. The slot is only free once
		// the renderer and its CEF children are gone, so wait for that before returning.
		try { _process.WaitForExit(1000); }
		catch (InvalidOperationException) { }
		try { _process.Kill(true); }
		catch (Exception) { }
		try { _process.WaitForExit(3000); }
		catch (Exception) { }

		ClearOwner();
		DiagLog.Write("renderer stopped, owner record cleared (slot lock held until unload)");
	}

	private bool _hasExited = false;
	private int _checkingExited = 0; // This needs to be a numeric type for Interlocked.Exchange

	private bool HasProcessExited()
	{
		// Process.HasExited can be an expensive call (on some systems?), so it's
		// offloaded to a Task, here. This could be related to Riot's Vanguard
		// kernel anti-cheat. The performance bottleneck occurs in ntdll, so this
		// is difficult to isolate and debug.
		Task.Run(() =>
		{
			if (!_hasExited && 0 == Interlocked.Exchange(ref _checkingExited, 1))
			{
				try
				{
					_hasExited = _process.HasExited;
				}
				catch (Exception e)
				{
					Services.PluginLog.Error(e, "Failed to get process exit status");
				}
				finally
				{
					Interlocked.Exchange(ref _checkingExited, 0);
				}
			}
		});

		return _hasExited;
	}

	private Process SetupProcess()
	{
		string cefAssemblyDir = _dependencyManager.GetDependencyPathFor("cef");

		RenderParams processArgs = new()
		{
			ParentPid = _parentPid,
			DalamudAssemblyDir = Path.GetDirectoryName(typeof(IPluginLog).Assembly.Location)!,
			CefAssemblyDir = cefAssemblyDir,
			CefCacheDir = _cefCacheDir,
			DxgiAdapterLuidLow = DxHandler.AdapterLuid.LowPart,
			DxgiAdapterLuidHigh = DxHandler.AdapterLuid.HighPart,
			KeepAliveHandleName = _keepAliveHandleName,
			IpcChannelName = _ipcChannelName
		};

		Process process = new();
		process.StartInfo = new ProcessStartInfo
		{
			FileName = Path.Combine(_pluginDir, "renderer", "Browsingway.Renderer.exe"),
			Arguments = RenderParamsSerializer.Serialize(processArgs),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		process.OutputDataReceived += (_, args) =>
		{
			Services.PluginLog.Info($"[Render]: {args.Data}");
			DiagLog.Write($"out: {args.Data}");
		};
		process.ErrorDataReceived += (_, args) =>
		{
			Services.PluginLog.Error($"[Render]: {args.Data}");
			DiagLog.Write($"err: {args.Data}");
		};

		return process;
	}

	private void OnProcessCrashed()
	{
		Crashed?.Invoke(this, EventArgs.Empty);
	}
}