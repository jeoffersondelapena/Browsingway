namespace Browsingway;

// The renderer restarts on a background task while the draw thread is still sending, so the value
// is never handed out; uses and swaps share one lock.
internal sealed class Guarded<T> where T : class
{
	private readonly object _lock = new();
	private T? _value;

	public bool HasValue { get { lock (_lock) { return _value is not null; } } }

	/// <summary>Runs <paramref name="use"/> on the current value, or does nothing if there is none.</summary>
	public bool Use(Action<T> use)
	{
		lock (_lock)
		{
			if (_value is null) { return false; }

			use(_value);
			return true;
		}
	}

	/// <summary>Disposes the current value and installs a fresh one.</summary>
	public void Replace(Func<T> create, Action<T> dispose)
	{
		lock (_lock)
		{
			Discard(dispose);
			_value = create();
		}
	}

	public void Clear(Action<T> dispose)
	{
		lock (_lock)
		{
			Discard(dispose);
		}
	}

	private void Discard(Action<T> dispose)
	{
		T? old = _value;
		_value = null;
		if (old is not null) { dispose(old); }
	}
}
