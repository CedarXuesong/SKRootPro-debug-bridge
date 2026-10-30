using System.Buffers.Binary;

namespace SkpdbDesktop;

// Mode: 0 mouse, 1 view, 2 held look/observe. Dimensions use display coordinates.
public sealed record ControlOverlayState(int Width, int Height, int Rotation,
    int Active, int Capacity, int Pending, double SamplingHz, double RttMs,
    byte Mode, bool InputActive)
{
    internal byte[] Encode(bool enabled)
    {
        if (Width is < 1 or > 65535 || Height is < 1 or > 65535 || Rotation is < 0 or > 3 ||
            Capacity is < 1 or > 65535 || Active < 0 || Active > Capacity || Pending is < 0 or > 65535 ||
            !double.IsFinite(SamplingHz) || SamplingHz < 0 || !double.IsFinite(RttMs) || RttMs < 0 || Mode > 2)
            throw new ArgumentOutOfRangeException(nameof(ControlOverlayState));
        var body = new byte[19];
        body[0] = 3; body[1] = enabled ? (byte)1 : (byte)0; body[2] = (byte)Rotation;
        var values = new[] { Width, Height, Active, Capacity, Pending,
            (int)Math.Round(Math.Min(6553.5, SamplingHz) * 10), (int)Math.Round(Math.Min(6553.5, RttMs) * 10) };
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3 + i * 2), (ushort)values[i]);
        body[17] = Mode; body[18] = InputActive ? (byte)1 : (byte)0;
        return body;
    }
}

/// <summary>One independent, bounded heartbeat stream; cursor visibility never owns this lifetime.</summary>
public sealed class ControlOverlaySession : IAsyncDisposable
{
    private readonly SkpdbClient _primary;
    private readonly long _generation;
    private readonly CancellationTokenSource _stop = new();
    private ControlOverlayState _state;
    private SkpdbClient? _channel;
    private Task _loop = Task.CompletedTask;
    private Exception? _failure;
    public Exception? Failure => Volatile.Read(ref _failure);
    public event Action<Exception>? Failed;

    private ControlOverlaySession(SkpdbClient primary, ControlOverlayState state)
    { _primary = primary; _generation = primary.ConnectionGeneration; _state = state; }

    public static async Task<ControlOverlaySession> StartAsync(SkpdbClient primary,
        ControlOverlayState state, CancellationToken cancellationToken = default)
    {
        var session = new ControlOverlaySession(primary, state);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            session._channel = await primary.OpenIndependentChannelAsync(deadline.Token).ConfigureAwait(false);
            await session._channel.ControlOverlayAsync(state, true, deadline.Token).ConfigureAwait(false);
            session._loop = session.RunAsync();
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public void Update(ControlOverlayState state) => Volatile.Write(ref _state, state);

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, _stop.Token).ConfigureAwait(false);
                try { await SendAsync().ConfigureAwait(false); }
                catch when (!_stop.IsCancellationRequested && _primary.IsConnected && _primary.ConnectionGeneration == _generation)
                {
                    _channel?.Dispose(); _channel = null;
                    using var retry = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    retry.CancelAfter(TimeSpan.FromSeconds(2));
                    _channel = await _primary.OpenIndependentChannelAsync(retry.Token).ConfigureAwait(false);
                    await SendAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Volatile.Write(ref _failure, error); Failed?.Invoke(error); }
    }

    private async Task SendAsync()
    {
        if (!_primary.IsConnected || _primary.ConnectionGeneration != _generation)
            throw new InvalidOperationException("调试显示层所属设备连接已变化");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1.5));
        await _channel!.ControlOverlayAsync(Volatile.Read(ref _state), true, deadline.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop.ConfigureAwait(false);
        if (_channel is { IsConnected: true })
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _channel.ControlOverlayAsync(Volatile.Read(ref _state), false, deadline.Token).ConfigureAwait(false);
            }
            catch { /* A disconnected renderer also hides the HUD after its six-second lease. */ }
        }
        _channel?.Dispose(); _channel = null;
        _stop.Dispose();
    }
}
