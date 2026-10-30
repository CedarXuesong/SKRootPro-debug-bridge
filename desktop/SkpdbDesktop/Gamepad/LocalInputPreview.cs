namespace SkpdbDesktop;

/// <summary>Offline execution of the production samplers, with no client or transport.</summary>
public sealed class LocalInputPreview
{
    private readonly Random _random;
    private readonly Dictionary<string, SampledButtonAction> _contacts = new();
    public int ActiveMoveIndex { get; private set; }
    public int ActiveLookIndex { get; private set; }
    private readonly TouchDeviceInfo _device;
    private readonly TouchDeviceInfo _samplingDevice;
    public TouchSlotAllocator Slots { get; }
    private readonly GamepadProfile _profile;
    private readonly List<TouchBatchEvent> _batch = new();
    public SampledJoystick Move { get; }
    public LookTouchRelay Look { get; }
    public TouchTraceBuffer Trace { get; } = new() { Enabled = true };
    public long Samples { get; private set; }
    public double TimeMs { get; private set; }

    public LocalInputPreview(TouchDeviceInfo device, GamepadProfile profile, Random? random = null)
    {
        _device = device;
        _samplingDevice = TouchSlotAllocator.SamplingDevice(device);
        Slots = new TouchSlotAllocator(device.SlotMax);
        _profile = profile.Clone();
        _profile.Validate();
        _random = random ?? Random.Shared;
        Move = new SampledJoystick(_random);
        Look = new LookTouchRelay(_random);
        Reset();
    }

    public void Reset()
    {
        _batch.Clear();
        Move.Release(_batch);
        Look.Release(_batch);
        Move.Configure(_samplingDevice, _profile.Move);
        Look.Configure(_samplingDevice, _profile, allowOverlap: _device.SlotMax > 0);
        foreach (var contact in _contacts.Values) contact.Release(_batch);
        Slots.ReleaseAll();
        ActiveMoveIndex = ActiveLookIndex = 0;
        Trace.Drain();
        Samples = 0;
        TimeMs = 0;
    }

    public void Step(double nowMs, double moveX, double moveY, bool sprint,
        double lookX, double lookY, bool observeHeld = false, bool releaseLook = false, IReadOnlySet<string>? pressedButtons = null)
    {
        if (!double.IsFinite(nowMs) || nowMs < TimeMs ||
            !double.IsFinite(moveX) || !double.IsFinite(moveY) ||
            !double.IsFinite(lookX) || !double.IsFinite(lookY))
            throw new ArgumentOutOfRangeException(nameof(nowMs), "预览需要单调递增的时间和有限位移");
        _batch.Clear();
        if (!Move.IsDown && (moveX != 0 || moveY != 0))
        {
            var selected = _profile.ChooseMove(_random);
            ActiveMoveIndex = _profile.Moves.ToList().IndexOf(selected);
            Move.Configure(_samplingDevice, selected);
        }
        Move.Update(moveX, moveY, sprint, nowMs, _batch);
        UpdateButtons(pressedButtons, nowMs);
        if (!Look.HasGesture && !releaseLook && (lookX != 0 || lookY != 0 || observeHeld))
        {
            var selected = _profile.ChooseLook(_random);
            ActiveLookIndex = _profile.Looks.ToList().IndexOf(selected);
            Look.Configure(_samplingDevice, _profile, selected, allowOverlap: _device.SlotMax > 0, preserveRemainder: true);
        }
        Look.Update(lookX, lookY, nowMs, true, observeHeld, releaseLook, _batch);
        Trace.Record(_device, Slots.Process(_batch), nowMs);
        TimeMs = nowMs;
        Samples++;
    }

    public void ReleaseAll()
    {
        _batch.Clear();
        Move.Release(_batch);
        Look.Release(_batch);
        foreach (var contact in _contacts.Values) contact.Release(_batch);
        var physical = Slots.Process(_batch, promotePending: false);
        physical.AddRange(Slots.ReleaseAll());
        Trace.Record(_device, physical, TimeMs);
    }
    private void UpdateButtons(IReadOnlySet<string>? pressed, double nowMs)
    {
        foreach (var button in _profile.Buttons)
        {
            if (!_contacts.TryGetValue(button.Id, out var action))
                _contacts[button.Id] = action = new SampledButtonAction(_samplingDevice, button, _profile.Move, _random);
            var down = pressed?.Contains(button.Id) == true || pressed?.Contains(button.Binding) == true;
            action.Update(down, nowMs, _batch);
            if (action.HeldTouch is { } held && !_batch.Any(e => e.Slot == held.Slot)) _batch.Add(held);
        }
    }
}
