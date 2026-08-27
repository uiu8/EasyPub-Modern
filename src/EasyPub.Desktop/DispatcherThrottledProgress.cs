using System.Windows.Threading;

namespace EasyPub.Desktop;

/// <summary>
/// Keeps high-frequency worker progress off the WPF dispatcher and delivers only
/// the latest snapshot at a bounded cadence.
/// </summary>
internal sealed class DispatcherThrottledProgress<T> : IProgress<T>, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<T> _handler;
    private readonly TimeSpan _interval;
    private readonly Lock _sync = new();
    private readonly Timer _timer;
    private T? _latest;
    private bool _hasValue;
    private bool _scheduled;
    private bool _disposed;
    private long _version;
    private long _deliveredVersion;

    public DispatcherThrottledProgress(Dispatcher dispatcher, TimeSpan interval, Action<T> handler)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _interval = interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval));
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Report(T value)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _latest = value;
            _hasValue = true;
            _version++;
            if (_scheduled) return;
            _scheduled = true;
            _timer.Change(_interval, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        T? value;
        long version;
        lock (_sync)
        {
            if (!_hasValue) return;
            value = _latest;
            version = _version;
            _latest = default;
            _hasValue = false;
            _scheduled = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        Deliver(value!, version, synchronous: true);
    }

    private void OnTimer(object? state)
    {
        T? value;
        long version;
        lock (_sync)
        {
            if (_disposed || !_hasValue)
            {
                _scheduled = false;
                return;
            }
            value = _latest;
            version = _version;
            _latest = default;
            _hasValue = false;
            _scheduled = false;
        }

        Deliver(value!, version, synchronous: false);
    }

    private void Deliver(T value, long version, bool synchronous)
    {
        void Apply()
        {
            lock (_sync)
            {
                if (_disposed || version <= _deliveredVersion) return;
                _deliveredVersion = version;
            }
            _handler(value);
        }

        if (synchronous)
        {
            if (_dispatcher.CheckAccess()) Apply();
            else _dispatcher.Invoke(Apply);
        }
        else
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, Apply);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _hasValue = false;
            _scheduled = false;
        }
        _timer.Dispose();
    }
}
