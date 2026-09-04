namespace Browsingway.Renderer;

// IPC connects before DX and CEF, so a message can arrive before there is anything to handle it.
internal sealed class ReadyGate
{
	private readonly ManualResetEventSlim _gate = new(false);
	private volatile bool _abandoned;

	public bool IsOpen => _gate.IsSet && !_abandoned;

	public void Open() => _gate.Set();

	public void Abandon()
	{
		_abandoned = true;
		_gate.Set();
	}

	/// <summary>Blocks until the renderer is usable. False means it never will be.</summary>
	public bool WaitUntilOpen()
	{
		_gate.Wait();
		return !_abandoned;
	}
}
