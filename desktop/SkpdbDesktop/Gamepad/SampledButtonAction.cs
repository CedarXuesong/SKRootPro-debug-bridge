namespace SkpdbDesktop;

/// <summary>Shared production/preview timing for every non-mode control.</summary>
public sealed class SampledButtonAction
{
    private enum Gesture { Tap, Rapid, Swipe, Pulse }
    private readonly GamepadButton _button;
    private readonly TouchDeviceInfo _device;
    private readonly SampledButtonContact _contact;
    private readonly SampledJoystick? _stick;
    private readonly Queue<(Gesture Kind, int Direction)> _pending = new();
    private bool _wasPressed, _toggle, _active, _tapReleased;
    private Gesture _gesture;
    private double _lastTime, _startedAt, _duration, _rapidAt;
    private int _startX, _startY, _endX, _endY;

    public bool IsDown => _contact.HeldTouch is not null || _stick?.IsDown == true;
    public TouchBatchEvent? HeldTouch => _stick?.HeldTouch ?? _contact.HeldTouch;
    public int PendingCount => _pending.Count;

    public SampledButtonAction(TouchDeviceInfo device, GamepadButton button,
        GamepadMoveSettings motion, Random? random = null)
    {
        _device = device; _button = button;
        _contact = new SampledButtonContact(random);
        if (button.Kind == GamepadControlKind.DPad)
        {
            var settings = motion.Clone();
            settings.Enabled = true; settings.CenterX = button.X; settings.CenterY = button.Y;
            settings.Radius = button.Distance; settings.Slot = button.Slot;
            _stick = new SampledJoystick(random); _stick.Configure(device, settings);
        }
    }

    public void Pulse(int direction)
    {
        if (direction != 0 && _button.Kind is GamepadControlKind.Scroll or GamepadControlKind.Zoom)
            Enqueue(Gesture.Pulse, Math.Sign(direction));
    }

    private void Enqueue(Gesture kind, int direction = 1)
    {
        // Keep short presses while another gesture finishes without permitting
        // a stalled transport to accumulate an unbounded replay queue.
        if (_pending.Count < 64) _pending.Enqueue((kind, direction));
    }

    public void Update(bool pressed, double nowMs, List<TouchBatchEvent> batch,
        (double X, double Y)? vector = null)
    {
        if (!double.IsFinite(nowMs)) throw new ArgumentOutOfRangeException(nameof(nowMs));
        nowMs = Math.Max(nowMs, _lastTime); _lastTime = nowMs;
        var rising = pressed && !_wasPressed;
        _wasPressed = pressed;
        if (_button.Kind is GamepadControlKind.Look or GamepadControlKind.Observe) return;
        if (_stick is not null)
        {
            var direction = vector ?? (_button.DirectionX, _button.DirectionY);
            _stick.Update(pressed ? direction.X : 0, pressed ? direction.Y : 0, false, nowMs, batch);
            return;
        }
        var point = _button.Kind is GamepadControlKind.Tap or GamepadControlKind.Skill or
            GamepadControlKind.Fire or GamepadControlKind.RightWalk or GamepadControlKind.Crosshair;
        var sustainedTap = _button.Kind is GamepadControlKind.Tap or GamepadControlKind.Skill &&
            _button.Mode == GamepadButtonMode.Hold;
        var rapid = _button.Kind == GamepadControlKind.Rapid || point && _button.Mode == GamepadButtonMode.Rapid;
        var hold = point && !sustainedTap;
        if (hold && !rapid)
        {
            if (_button.Mode == GamepadButtonMode.Toggle && rising) _toggle = !_toggle;
            var down = _button.Mode == GamepadButtonMode.Toggle ? _toggle : pressed;
            if (down) _contact.Press(_device, _button, batch); else _contact.Release(batch);
            return;
        }
        if (rapid)
        {
            if (rising) _rapidAt = nowMs;
            if (pressed && !_active && _pending.Count == 0 && nowMs >= _rapidAt)
            { Enqueue(Gesture.Rapid); _rapidAt = nowMs + _button.TapIntervalMs; }
        }
        else if (rising)
            Enqueue(_button.Kind switch
            {
                GamepadControlKind.Swipe => Gesture.Swipe,
                GamepadControlKind.Scroll or GamepadControlKind.Zoom => Gesture.Pulse,
                _ => Gesture.Tap
            });

        if (_active)
        {
            // A normal key mirrors its physical press, with a minimum tap duration.
            // Once released, a later press is a separate queued gesture and must
            // never revive the old contact or change its randomized landing.
            if (!pressed) _tapReleased = true;
            var progress = Math.Clamp((nowMs - _startedAt) / _duration, 0, 1);
            var eased = progress * progress * (3 - 2 * progress);
            if (_endX != _startX || _endY != _startY)
                _contact.MoveTo(_device, (int)Math.Round(_startX + (_endX - _startX) * eased),
                    (int)Math.Round(_startY + (_endY - _startY) * eased), batch);
            if (progress >= 1 && !(_gesture == Gesture.Tap && sustainedTap && !_tapReleased))
            { _contact.Release(batch); _active = false; }
            return; // A queued gesture starts on a later sample, never in this UP's batch.
        }
        if (!_pending.TryDequeue(out var next)) return;
        _contact.Press(_device, _button, batch);
        var held = _contact.HeldTouch!.Value;
        (_startX, _startY) = _device.ToLogical(held.X, held.Y);
        _endX = _startX; _endY = _startY;
        if (next.Kind == Gesture.Swipe)
        {
            _endX = Math.Clamp((int)Math.Round(_startX + _button.DirectionX * _button.Distance * (_device.DisplayWidth - 1)), 0, _device.DisplayWidth - 1);
            _endY = Math.Clamp((int)Math.Round(_startY + _button.DirectionY * _button.Distance * (_device.DisplayHeight - 1)), 0, _device.DisplayHeight - 1);
        }
        else if (next.Kind == Gesture.Pulse)
            _endY = Math.Clamp((int)Math.Round(_startY - next.Direction * _button.Amount * (_device.DisplayHeight - 1)), 0, _device.DisplayHeight - 1);
        _duration = next.Kind switch
        {
            Gesture.Tap => 40,
            Gesture.Rapid => Math.Clamp(_button.TapIntervalMs / 2.0, 8, 40),
            _ => _button.DurationMs
        };
        _gesture = next.Kind;
        _tapReleased = !pressed || _pending.Count != 0;
        _startedAt = nowMs; _active = true;
    }

    public void Release(List<TouchBatchEvent> batch)
    {
        _stick?.Release(batch); _contact.Release(batch);
        _pending.Clear(); _active = _toggle = _wasPressed = _tapReleased = false;
        _lastTime = _rapidAt = 0;
    }
}
