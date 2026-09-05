using Browsingway.Common;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;

namespace Browsingway;

public class Plugin : IDalamudPlugin
{
	private const string _command = "/bw";

	private readonly DependencyManager _dependencyManager;
	private readonly Dictionary<Guid, Overlay> _overlays = new();
	private readonly string _pluginConfigDir;
	private readonly string _pluginDir;

	private RenderProcess? _renderProcess;
	private Timer? _readyWatchdog;
	private ICallGateProvider<bool>? _ipcHealthy;
	private ICallGateProvider<string>? _ipcStatus;
	private ICallGateProvider<string, bool>? _ipcRestart;
	private ICallGateProvider<bool>? _ipcReloadOverlays;
	private ICallGateProvider<int>? _ipcPort;
	private DateTime _rendererStartAt;
	private volatile bool _rendererReady;
	private const int _readyTimeoutMs = 20000;
	private ActHandler _actHandler;
	private Settings? _settings;
	private Services _services;

	public Plugin(IDalamudPluginInterface pluginInterface)
	{
		// init services
		_services = pluginInterface.Create<Services>()!;

		_pluginDir = pluginInterface.AssemblyLocation.DirectoryName ?? "";
		if (String.IsNullOrEmpty(_pluginDir))
		{
			throw new Exception("Could not determine plugin directory");
		}

		_pluginConfigDir = pluginInterface.GetPluginConfigDirectory();

		_actHandler = new ActHandler();

		_dependencyManager = new DependencyManager(_pluginDir, _pluginConfigDir);
		_dependencyManager.DependenciesReady += (_, _) => DependenciesReady();
		_dependencyManager.Initialise();

		// Hook up render hook
		pluginInterface.UiBuilder.Draw += Render;
	}

	// Required for LivePluginLoader support
	public string AssemblyLocation { get; } = Assembly.GetExecutingAssembly().Location;
	public string Name => "Browsingway";

	public void Dispose()
	{
		foreach (Overlay overlay in _overlays.Values) { overlay.Dispose(); }

		_overlays.Clear();

		Services.Framework.Update -= StartRendererWhenQuiet;
		_readyWatchdog?.Dispose();
		_ipcHealthy?.UnregisterFunc();
		_ipcStatus?.UnregisterFunc();
		_ipcRestart?.UnregisterFunc();
		_ipcReloadOverlays?.UnregisterFunc();
		_ipcPort?.UnregisterFunc();
		_renderProcess?.Dispose();

		_settings?.Dispose();

		Services.CommandManager.RemoveHandler(_command);

		WndProcHandler.Shutdown();
		DxHandler.Shutdown();

		_dependencyManager.Dispose();
	}

	private void DependenciesReady()
	{
		// Spin up DX handling from the plugin interface
		DxHandler.Initialise(Services.PluginInterface);

		// Spin up WndProc hook
		WndProcHandler.Initialise(DxHandler.WindowHandle);
		WndProcHandler.WndProcMessage += OnWndProc;

		// Boot the render process. This has to be done before initialising settings to prevent a
		// race condition overlays receiving a null reference.
		int pid = Process.GetCurrentProcess().Id;
		_renderProcess = new RenderProcess(pid, _pluginDir, _pluginConfigDir, _dependencyManager, Services.PluginLog);
		_renderProcess.RendererReady += msg =>
		{
			_rendererReady = true;
			if (!msg.HasDxSharedTexturesSupport)
			{
				Services.PluginLog.Error("Could not initialize shared textures transport. Browsingway will not work.");
				Services.Chat.PrintError("Browsingway: shared textures unsupported; overlays cannot work.");
				return;
			}

			Services.Framework.RunOnFrameworkThread(() =>
			{
				if (_settings is not null)
				{
					_settings.HydrateOverlays();
					int port = CacheSlotPolicy.PortForSlot(_renderProcess.CacheSlot);
					uint restarts = _renderProcess.RestartCount;
					Services.Chat.Print(restarts == 0
						? $"Browsingway: overlays ready on port {port}."
						: $"Browsingway: overlays recovered after {restarts} renderer restart(s) on port {port}.");
				}
			});
		};
		_renderProcess.SetCursor += msg =>
		{
			Services.Framework.RunOnFrameworkThread(() =>
			{
				Guid guid = new(msg.Guid.Span);
				Overlay? overlay = _overlays.Values.FirstOrDefault(overlay => overlay.RenderGuid == guid);
				overlay?.SetCursor(msg.Cursor);
			});
		};
		_renderProcess.UpdateTexture += msg =>
		{
			Services.Framework.RunOnFrameworkThread(() =>
			{
				Guid guid = new(msg.Guid.Span);
				if (_overlays.TryGetValue(guid, out Overlay? overlay))
				{
					overlay.SetTexture((IntPtr)msg.TextureHandle);
				}
				else
				{
					Services.PluginLog.Error("Overlay Id not found");
				}
			});
		};
		// A restart that comes back healthy re-arms this; a give-up does not, since it reports itself.
		_renderProcess.Crashed += (_, _) =>
		{
			if (_renderProcess.IsRunning) { ArmReadyWatchdog(); }
		};
		// Wine's spawn forks with the allocator locks held; doing it mid plugin-load burst has hung the game.
		TimeSpan gameAge = DateTime.Now - Process.GetCurrentProcess().StartTime;
		int delayMs = RendererStartPolicy.DelayMs(gameAge);
		_rendererStartAt = DateTime.Now.AddMilliseconds(delayMs);
		DiagLog.Write(delayMs > 0
			? $"renderer start deferred {delayMs / 1000}s past plugin load"
			: $"renderer starting now; the game has been up for {gameAge.TotalMinutes:F0} min");
		Services.Framework.Update += StartRendererWhenQuiet;

		// Prep settings
		_settings = new Settings(CacheSlotPolicy.PortForSlot(_renderProcess.CacheSlot));
		if (_settings is not null)
		{
			_settings.OverlayAdded += OnOverlayAdded;
			_settings.OverlayNavigated += OnOverlayNavigated;
			_settings.OverlayDebugged += OnOverlayDebugged;
			_settings.OverlayRemoved += OnOverlayRemoved;
			_settings.OverlayZoomed += OnOverlayZoomed;
			_settings.OverlayMuted += OnOverlayMuted;
			_actHandler.AvailabilityChanged += OnActAvailabilityChanged;
			_settings.OverlayUserCssChanged += OnUserCssChanged;
		}

		// Hook up the main BW command
		// Overlay Doctor asks these to decide whether the renderer needs a respawn.
		_ipcHealthy = Services.PluginInterface.GetIpcProvider<bool>("Browsingway.Healthy");
		_ipcStatus = Services.PluginInterface.GetIpcProvider<string>("Browsingway.Status");
		_ipcRestart = Services.PluginInterface.GetIpcProvider<string, bool>("Browsingway.Restart");
		_ipcHealthy.RegisterFunc(() => OverlayStatus.Healthy(_renderProcess.IsRunning, _rendererReady));
		_ipcStatus.RegisterFunc(StatusLine);
		_ipcRestart.RegisterFunc(RestartRenderer);
		_ipcReloadOverlays = Services.PluginInterface.GetIpcProvider<bool>("Browsingway.ReloadOverlays");
		_ipcReloadOverlays.RegisterFunc(ReloadOverlays);
		// IINACT binds the same port this window's overlays will dial, instead of whatever the shared config holds.
		_ipcPort = Services.PluginInterface.GetIpcProvider<int>("Browsingway.Port");
		_ipcPort.RegisterFunc(() => _renderProcess is null ? 0 : CacheSlotPolicy.PortForSlot(_renderProcess.CacheSlot));

		Services.CommandManager.AddHandler(_command,
			new CommandInfo(HandleCommand) {HelpMessage = "Control Browsingway from the chat line! Type '/bw config' or open the settings for more info.", ShowInHelp = true});
	}

	private void StartRendererWhenQuiet(IFramework framework)
	{
		if (DateTime.Now < _rendererStartAt) { return; }

		Services.Framework.Update -= StartRendererWhenQuiet;
		_renderProcess?.Start();
		ArmReadyWatchdog();
	}

	private string StatusLine()
	{
		if (_renderProcess is null) { return "renderer not created yet"; }
		return OverlayStatus.Describe(_renderProcess.IsRunning, _rendererReady, CacheSlotPolicy.PortForSlot(_renderProcess.CacheSlot), _renderProcess.RestartCount);
	}

	private bool RestartRenderer(string reason)
	{
		if (_renderProcess is null) { return false; }
		Services.Chat.Print($"Browsingway: restarting the renderer ({reason}).");
		_rendererReady = false;
		Task.Run(() =>
		{
			if (!_renderProcess.Restart(reason))
			{
				Services.Framework.RunOnFrameworkThread(() => Services.Chat.PrintError("Browsingway: renderer restart failed; use /xldisableplugintemp Browsingway then /xlenableplugintemp Browsingway."));
			}
		});
		return true;
	}

	private bool ReloadOverlays()
	{
		if (_settings is null) { return false; }
		Services.Framework.RunOnFrameworkThread(() =>
		{
			_settings.ReloadAllOverlays();
			Services.Chat.Print("Browsingway: overlay pages reloaded.");
		});
		return true;
	}

	private void ArmReadyWatchdog()
	{
		_rendererReady = false;
		_readyWatchdog?.Dispose();
		_readyWatchdog = new Timer(_ =>
		{
			if (_rendererReady) { return; }

			Services.Framework.RunOnFrameworkThread(() =>
				Services.Chat.PrintError($"Browsingway: renderer started but never reported ready after {_readyTimeoutMs / 1000}s; overlays are not up."));
		}, null, _readyTimeoutMs, Timeout.Infinite);
	}

	private (bool, long) OnWndProc(WindowsMessage msg, ulong wParam, long lParam)
	{
		// Notify all the overlays of the wndproc, respond with the first capturing response (if any)
		// TODO: Yeah this ain't great but realistically only one will capture at any one time for now.
		IEnumerable<(bool, long)> responses = _overlays.Select(pair => pair.Value.WndProcMessage(msg, wParam, lParam));
		return responses.FirstOrDefault(pair => pair.Item1);
	}

	private void OnActAvailabilityChanged(object? sender, bool e)
	{
		_settings?.OnActAvailabilityChanged(e);
	}

	private void OnOverlayAdded(object? sender, InlayConfiguration overlayConfig)
	{
		if (_renderProcess is null || _settings is null)
		{
			return;
		}

		Overlay overlay = new(_renderProcess, overlayConfig, _pluginDir);
		_overlays.TryAdd(overlayConfig.Guid, overlay);
	}

	private void OnOverlayNavigated(object? sender, InlayConfiguration config)
	{
		if (_overlays.TryGetValue(config.Guid, out var overlay))
			overlay.Navigate(config.Url);
	}

	private void OnOverlayDebugged(object? sender, InlayConfiguration config)
	{
		if (_overlays.TryGetValue(config.Guid, out var overlay))
			overlay.Debug();
	}

	private void OnOverlayRemoved(object? sender, InlayConfiguration config)
	{
		if (_overlays.Remove(config.Guid, out var overlay))
		{
			overlay.Dispose();
		}
	}

	private void OnOverlayZoomed(object? sender, InlayConfiguration config)
	{
		if (_overlays.TryGetValue(config.Guid, out var overlay))
			overlay.Zoom(config.Zoom);
	}

	private void OnOverlayMuted(object? sender, InlayConfiguration config)
	{
		if (_overlays.TryGetValue(config.Guid, out var overlay))
			overlay.Mute(config.Muted);
	}

	private void OnUserCssChanged(object? sender, InlayConfiguration config)
	{
		Overlay overlay = _overlays[config.Guid];
		overlay.InjectUserCss(config.CustomCss);
	}

	private void Render()
	{
		_dependencyManager.Render();
		_settings?.Render();

		ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));

		_renderProcess?.EnsureRenderProcessIsAlive();
		_actHandler.Check();

		foreach (Overlay overlay in _overlays.Values) { overlay.Render(); }

		ImGui.PopStyleVar();
	}

	private void HandleCommand(string command, string rawArgs)
	{
		// Docs complain about perf of multiple splits.
		// I'm not convinced this is a sufficiently perf-critical path to care.
		string[] args = rawArgs.Split(null as char[], 2, StringSplitOptions.RemoveEmptyEntries);

		if (args.Length == 0)
		{
			Services.Chat.PrintError(
				"No subcommand specified. Valid subcommands are: config,overlay,status,restart,reload.");
			return;
		}

		string subcommandArgs = args.Length > 1 ? args[1] : "";

		switch (args[0])
		{
			case "config":
				_settings?.HandleConfigCommand(subcommandArgs);
				break;
			case "inlay":
				_settings?.HandleOverlayCommand(subcommandArgs);
				break;
			case "overlay":
				_settings?.HandleOverlayCommand(subcommandArgs);
				break;
			case "status":
				Services.Chat.Print($"Browsingway: {StatusLine()}.");
				break;
			case "restart":
				RestartRenderer("requested");
				break;
			case "reload":
				ReloadOverlays();
				break;
			default:
				Services.Chat.PrintError(
					$"Unknown subcommand '{args[0]}'. Valid subcommands are: config,overlay,inlay,status,restart,reload.");
				break;
		}
	}
}