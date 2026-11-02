namespace SkpdbDesktop;

/// <summary>Owns a socket-bound, renewable lease for conflicting phone gestures.</summary>
public sealed class GestureLeaseSession : IAsyncDisposable
{
    private readonly SkpdbClient _primary, _channel;
    private readonly long _generation;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private Exception? _failure;
    private int _disposed;
    public bool Changed { get; private set; }
    public Exception? Failure => Volatile.Read(ref _failure);
    public event Action<Exception>? Failed;

    private GestureLeaseSession(SkpdbClient primary, SkpdbClient channel, long generation)
    { _primary = primary; _channel = channel; _generation = generation; }

    public static async Task<GestureLeaseSession> StartAsync(SkpdbClient primary, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        var generation = primary.ConnectionGeneration;
        var channel = await primary.OpenIndependentChannelAsync(deadline.Token).ConfigureAwait(false);
        var session = new GestureLeaseSession(primary, channel, generation);
        try
        {
            session.Changed = await channel.SetGestureLeaseAsync(true, deadline.Token).ConfigureAwait(false);
            if (!primary.IsConnected || primary.ConnectionGeneration != generation)
                throw new InvalidOperationException("系统手势协调所属设备连接已变化");
            session._loop = session.RunAsync();
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(1500, _stop.Token).ConfigureAwait(false);
                if (!_primary.IsConnected || _primary.ConnectionGeneration != _generation)
                    throw new InvalidOperationException("系统手势协调所属设备连接已变化");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(4));
                await _channel.SetGestureLeaseAsync(true, deadline.Token, renew: true).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Volatile.Write(ref _failure, error); Failed?.Invoke(error); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        await _loop.ConfigureAwait(false);
        try
        {
            if (_channel.IsConnected)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await _channel.SetGestureLeaseAsync(false, deadline.Token).ConfigureAwait(false);
            }
        }
        catch { /* EOF also releases this socket's lease; the journal survives daemon restart. */ }
        finally { _channel.Dispose(); _stop.Dispose(); }
    }
}
