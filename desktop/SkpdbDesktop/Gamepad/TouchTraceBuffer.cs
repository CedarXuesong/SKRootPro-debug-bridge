using System.Diagnostics;

namespace SkpdbDesktop;

public readonly record struct TouchTracePoint(double TimeMs, byte Action, ushort Slot,
    double X, double Y, int Width, int Height);

/// <summary>Bounded diagnostics queue. It never calls the UI or the transport.</summary>
public sealed class TouchTraceBuffer
{
    private readonly object _gate = new();
    private readonly Queue<TouchTracePoint> _points = new();
    private int _enabled;
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set { Volatile.Write(ref _enabled, value ? 1 : 0); if (!value) lock (_gate) _points.Clear(); }
    }
    public void Record(TouchDeviceInfo device, IReadOnlyList<TouchBatchEvent> events, double? timeMs = null)
    {
        if (!Enabled) return;
        var now = timeMs ?? Stopwatch.GetElapsedTime(0).TotalMilliseconds;
        lock (_gate)
        {
            if (!Enabled) return;
            foreach (var item in events)
            {
                var point = device.ToLogical(item.X, item.Y);
                _points.Enqueue(new(now, item.Action, item.Slot, point.X, point.Y, device.DisplayWidth, device.DisplayHeight));
                if (_points.Count > 8192) _points.Dequeue();
            }
        }
    }
    public TouchTracePoint[] Drain()
    {
        lock (_gate) { var result = _points.ToArray(); _points.Clear(); return result; }
    }
}
