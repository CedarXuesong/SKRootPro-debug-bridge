namespace SkpdbDesktop;

/// <summary>A new area-uniform landing per press; held snapshots retain that exact point.</summary>
public sealed class SampledButtonContact
{
    private readonly Random _random;
    private TouchBatchEvent? _held;
    public SampledButtonContact(Random? random = null) => _random = random ?? Random.Shared;
    public TouchBatchEvent? HeldTouch => _held;
    public void Press(TouchDeviceInfo device, GamepadButton button, List<TouchBatchEvent> batch)
    {
        if (_held is not null) return;
        var radius = button.Radius * Math.Min(device.DisplayWidth, device.DisplayHeight) * Math.Sqrt(_random.NextDouble());
        var angle = _random.NextDouble() * Math.Tau;
        var x = Math.Clamp((int)Math.Round(button.X * (device.DisplayWidth - 1) + radius * Math.Cos(angle)), 0, device.DisplayWidth - 1);
        var y = Math.Clamp((int)Math.Round(button.Y * (device.DisplayHeight - 1) + radius * Math.Sin(angle)), 0, device.DisplayHeight - 1);
        var native = device.ToNative(x, y);
        batch.Add(new(1, native.X, native.Y, (ushort)button.Slot));
        _held = new(2, native.X, native.Y, (ushort)button.Slot);
    }
    public void Release(List<TouchBatchEvent> batch)
    {
        if (_held is not { } held) return;
        batch.Add(held); batch.Add(new(3, 0, 0, held.Slot)); _held = null;
    }
    public void MoveTo(TouchDeviceInfo device, int logicalX, int logicalY, List<TouchBatchEvent> batch)
    {
        if (_held is not { } held) return;
        var point = device.ToNative(logicalX, logicalY);
        if (point.X == held.X && point.Y == held.Y) return;
        _held = new(2, point.X, point.Y, held.Slot);
        batch.Add(_held.Value);
    }
}
