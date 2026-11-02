namespace SkpdbDesktop;

/// <summary>
/// Converts relative movement to bounded drags for additive multi-touch look.
/// During a handoff the old finger eases out as the new finger eases in; their
/// combined displacement always equals the consumed mouse movement.
/// Time is supplied by the sampling loop so this state machine is testable.
/// </summary>
public sealed class LookTouchRelay
{
    private readonly Random _random;
    private TouchDeviceInfo _device = null!;
    private GamepadLookSettings _settings = null!;
    private int _minX, _maxX, _minY, _maxY, _anchorX, _anchorY;
    private int _x, _y, _nextX, _nextY;
    private double _remainingX, _remainingY, _lastTick, _lastInput, _handoffUntil;
    private double _plannedOverlapMs, _activeDownAt, _nextDownAt, _spareReadyAt;
    private double _shareRemainderX, _shareRemainderY;
    private int _endInsetX, _endInsetY;
    private bool _hasTick, _handoff;
    private ushort _primarySlot;

    public GamepadLookSettings Settings => _settings;
    public bool IsDown { get; private set; }
    public bool HasGesture { get; private set; }
    public ushort ActiveSlot { get; private set; }
    public ushort SpareSlot { get; private set; }
    public int ActiveCount => IsDown ? (_handoff ? 2 : 1) : 0;

    public LookTouchRelay(Random? random = null) => _random = random ?? Random.Shared;

    public void Configure(TouchDeviceInfo device, GamepadProfile profile, GamepadLookSettings? selected = null, bool allowOverlap = true,
        bool preserveRemainder = false)
    {
        var carryX = preserveRemainder ? _remainingX : 0;
        var carryY = preserveRemainder ? _remainingY : 0;
        _device = device;
        _settings = selected ?? profile.Look;
        if (_settings.Enabled && (_settings.Slot < 0 || _settings.Slot > device.SlotMax))
            throw new InvalidOperationException("视角 slot 超出触摸屏支持范围");
        _primarySlot = ActiveSlot = (ushort)Math.Max(0, _settings.Slot);
        SpareSlot = _primarySlot;
        var used = new HashSet<int>(profile.Buttons.Where(button => button.UsesTouchSlot).Select(button => button.Slot));
        if (profile.Moves.Any(m => m.Enabled)) used.Add(profile.Move.Slot);
        used.Add(_settings.Slot);
        for (var slot = Math.Min(9, device.SlotMax); allowOverlap && slot >= 0; slot--)
        {
            if (used.Contains(slot)) continue;
            SpareSlot = (ushort)slot;
            break;
        }
        // Production samplers use a logical namespace, so a full layout must
        // still have a distinct relay ID; physical leasing happens downstream.
        if (allowOverlap && SpareSlot == _primarySlot)
            for (var slot = 10; slot <= Math.Min(ushort.MaxValue, device.SlotMax); slot++)
            {
                if (used.Contains(slot)) continue;
                SpareSlot = (ushort)slot;
                break;
            }
        // Bounds must describe reachable screen pixels. Clamping only inside
        // ToNative leaves the state moving outside the screen while the actual
        // finger stalls against the physical edge.
        _minX = (int)Math.Ceiling(Math.Max(0, _settings.AnchorX - _settings.MaxRadiusX) * (device.DisplayWidth - 1));
        _maxX = (int)Math.Floor(Math.Min(1, _settings.AnchorX + _settings.MaxRadiusX) * (device.DisplayWidth - 1));
        _minY = (int)Math.Ceiling(Math.Max(0, _settings.AnchorY - _settings.MaxRadiusY) * (device.DisplayHeight - 1));
        _maxY = (int)Math.Floor(Math.Min(1, _settings.AnchorY + _settings.MaxRadiusY) * (device.DisplayHeight - 1));
        _maxX = Math.Max(_minX, _maxX);
        _maxY = Math.Max(_minY, _maxY);
        _anchorX = Math.Clamp((int)Math.Round(_settings.AnchorX * (device.DisplayWidth - 1)),
            _minX + (_maxX - _minX) / 5, _maxX - (_maxX - _minX) / 5);
        _anchorY = Math.Clamp((int)Math.Round(_settings.AnchorY * (device.DisplayHeight - 1)),
            _minY + (_maxY - _minY) / 5, _maxY - (_maxY - _minY) / 5);
        IsDown = _handoff = _hasTick = false;
        _remainingX = carryX; _remainingY = carryY;
        HasGesture = false;
        _shareRemainderX = _shareRemainderY = 0;
        _spareReadyAt = 0;
        _x = _anchorX; _y = _anchorY;
    }

    public IEnumerable<TouchBatchEvent> HeldTouches()
    {
        if (!IsDown) yield break;
        yield return Point(2, ActiveSlot, _x, _y);
        if (_handoff) yield return Point(2, OtherSlot(), _nextX, _nextY);
    }

    public void Release(List<TouchBatchEvent> batch, bool preserveRemainder = false)
    {
        var carryX = preserveRemainder ? _remainingX : 0;
        var carryY = preserveRemainder ? _remainingY : 0;
        var hadTouch = IsDown;
        var lastSlot = _handoff ? OtherSlot() : ActiveSlot;
        // The last UDP position may still be queued or lost. Flush both final
        // coordinates before either lift. Repeating a position adds no motion.
        if (IsDown) batch.Add(Point(2, ActiveSlot, _x, _y));
        if (_handoff) batch.Add(Point(2, OtherSlot(), _nextX, _nextY));
        if (IsDown) batch.Add(new TouchBatchEvent(3, 0, 0, ActiveSlot));
        if (_handoff) batch.Add(new TouchBatchEvent(3, 0, 0, OtherSlot()));
        IsDown = _handoff = _hasTick = false;
        _remainingX = carryX; _remainingY = carryY;
        HasGesture = false;
        _shareRemainderX = _shareRemainderY = 0;
        _spareReadyAt = 0;
        _x = _anchorX; _y = _anchorY;
        // Alternate the next gesture too, including short drags that never
        // reached the boundary. An explicit release still lifts immediately.
        if (hadTouch) ActiveSlot = lastSlot == _primarySlot ? SpareSlot : _primarySlot;
    }

    public void Update(double rawX, double rawY, double nowMs, bool continuous,
        bool observeHeld, bool releaseRequested, List<TouchBatchEvent> batch)
    {
        if (releaseRequested || !_settings.Enabled)
        {
            Release(batch);
            return;
        }
        var elapsed = _hasTick ? Math.Clamp(nowMs - _lastTick, 0.1, 100) : 4;
        _hasTick = true; _lastTick = nowMs;
        var hasInput = Math.Abs(rawX) + Math.Abs(rawY) > 0.000001;
        if (hasInput || observeHeld) HasGesture = true;
        if (hasInput) _lastInput = nowMs;
        _remainingX += rawX * _settings.Sensitivity * (_settings.InvertX ? -1 : 1);
        _remainingY += rawY * _settings.Sensitivity * (_settings.InvertY ? -1 : 1);

        // Integrate the filter's remainder on idle ticks too. Filtering only
        // nonzero samples loses the end of every flick and carries old velocity
        // into the next drag. Integer steps keep subpixel error across relays.
        var alpha = !hasInput && nowMs - _lastInput >= 16
            ? 1 : 1 - Math.Pow(_settings.Smoothing, elapsed / 4);
        var stepX = _remainingX * alpha;
        var stepY = _remainingY * alpha;
        var length = Math.Sqrt(stepX * stepX + stepY * stepY);
        var maxStep = Math.Max(1, Math.Min(24, Math.Min(_maxX - _minX, _maxY - _minY) / 3.0));
        if (length > maxStep) { stepX *= maxStep / length; stepY *= maxStep / length; }
        var dx = (int)Math.Truncate(stepX + Math.CopySign(1e-9, stepX));
        var dy = (int)Math.Truncate(stepY + Math.CopySign(1e-9, stepY));

        if (dx == 0 && dy == 0)
        {
            if (_handoff && nowMs >= _handoffUntil) FinishHandoff(batch, nowMs);
            if (!IsDown && observeHeld)
            {
                Down(batch, Start(_minX, _maxX, _anchorX, 0),
                    Start(_minY, _maxY, _anchorY, 0), nowMs);
                _lastInput = nowMs;
            }
            if (IsDown && !_handoff && !observeHeld && !hasInput && _settings.RecenterMode == "onIdle" &&
                nowMs - _lastInput >= _settings.RecenterDelayMs &&
                nowMs - _activeDownAt >= _settings.RelayOverlapMs &&
                Math.Abs(_remainingX) < 1.01 && Math.Abs(_remainingY) < 1.01)
                Release(batch, preserveRemainder: true);
            return;
        }

        if (!IsDown) Down(batch, Start(_minX, _maxX, _anchorX, 0),
            Start(_minY, _maxY, _anchorY, 0), nowMs);
        var relay = continuous || observeHeld || _settings.RecenterMode == "edge";
        var crossesBoundary = CrossesBoundary(_x, _y, dx, dy);
        if (relay && !_handoff && SpareSlot != _primarySlot &&
            ((hasInput && nowMs >= _spareReadyAt && NearEdge(dx, dy, elapsed)) || crossesBoundary))
            BeginHandoff(batch, dx, dy, nowMs);

        var (movedX, movedY) = Move(batch, dx, dy, nowMs);
        if (relay && (movedX != dx || movedY != dy))
        {
            if (_handoff) FinishHandoff(batch, nowMs);
            // Both contacts can run out of room during a fast reversal or in
            // a small region. Recycle the lifted slot while keeping one down,
            // and let its fresh contact consume this sample's remaining delta.
            if (SpareSlot != _primarySlot)
                BeginHandoff(batch, dx - movedX, dy - movedY, nowMs);
            else
            {
                // All slots are reserved: finish the old drag before starting
                // another one. Keep the unconsumed displacement for this tick.
                batch.Add(new TouchBatchEvent(3, 0, 0, ActiveSlot));
                IsDown = false;
                Down(batch, Start(_minX, _maxX, _anchorX, dx),
                    Start(_minY, _maxY, _anchorY, dy), nowMs);
            }
            var (extraX, extraY) = Move(batch, dx - movedX, dy - movedY, nowMs);
            movedX += extraX; movedY += extraY;
        }
        if (_handoff)
        {
            var oldBlocked = CrossesBoundary(_x, _y, dx, dy);
            var nextBlocked = CrossesBoundary(_nextX, _nextY, dx, dy);
            if (nextBlocked && !oldBlocked)
                CancelHandoff(batch, nowMs);
            else if (oldBlocked || nowMs >= _handoffUntil)
                FinishHandoff(batch, nowMs);
        }
        _remainingX -= movedX;
        _remainingY -= movedY;
        if (!relay)
        {
            if (movedX != dx) _remainingX = 0;
            if (movedY != dy) _remainingY = 0;
        }
    }

    private bool NearEdge(int dx, int dy, double elapsed)
    {
        // Prepare far enough ahead for the longer overlap. The per-contact
        // inset ends the old drag inside the region instead of at a fixed edge.
        // Sudden acceleration can still force an early transfer at the boundary.
        // The old finger covers half the overlap distance under smoothstep,
        // rather than duplicating the new finger's entire movement.
        var horizon = _plannedOverlapMs * .5 / Math.Max(4, elapsed);
        var marginX = Math.Min((_maxX - _minX) * 0.8, _endInsetX + Math.Max(24, Math.Abs(dx) * (horizon + 2)));
        var marginY = Math.Min((_maxY - _minY) * 0.8, _endInsetY + Math.Max(24, Math.Abs(dy) * (horizon + 2)));
        return (dx > 0 && _maxX - _x <= marginX) || (dx < 0 && _x - _minX <= marginX) ||
               (dy > 0 && _maxY - _y <= marginY) || (dy < 0 && _y - _minY <= marginY);
    }

    private int Start(int min, int max, int anchor, int direction)
    {
        var span = max - min;
        var center = direction > 0 ? min + span * .25 : direction < 0 ? max - span * .25 : anchor;
        var low = Math.Max(min + span * .20, center - span * _settings.RelayStartJitter);
        var high = Math.Min(max - span * .20, center + span * _settings.RelayStartJitter);
        // A bounded triangular distribution favors the interior. Randomness
        // only changes contact placement; it never adds camera movement.
        return Math.Clamp((int)Math.Round(low + (high - low) * Sample()), min, max);
    }

    private double Sample() => (_random.NextDouble() + _random.NextDouble()) * .5;

    private void PlanContact()
    {
        _plannedOverlapMs = _settings.RelayOverlapMs * (.8 + .4 * Sample());
        _endInsetX = (int)Math.Round((_maxX - _minX) * _settings.RelayEarlyEnd * (.5 + .5 * Sample()));
        _endInsetY = (int)Math.Round((_maxY - _minY) * _settings.RelayEarlyEnd * (.5 + .5 * Sample()));
    }

    private ushort OtherSlot() => ActiveSlot == _primarySlot ? SpareSlot : _primarySlot;

    private bool CrossesBoundary(int x, int y, int dx, int dy) =>
        x + dx < _minX || x + dx > _maxX || y + dy < _minY || y + dy > _maxY;

    private TouchBatchEvent Point(byte action, ushort slot, int x, int y)
    {
        var point = _device.ToNative(x, y);
        return new TouchBatchEvent(action, point.X, point.Y, slot);
    }

    private void Down(List<TouchBatchEvent> batch, int x, int y, double nowMs)
    {
        _x = x; _y = y; IsDown = true;
        _activeDownAt = nowMs;
        PlanContact();
        batch.Add(Point(1, ActiveSlot, x, y));
    }

    private void BeginHandoff(List<TouchBatchEvent> batch, int dx, int dy, double nowMs)
    {
        _nextX = Start(_minX, _maxX, _anchorX, dx);
        _nextY = Start(_minY, _maxY, _anchorY, dy);
        batch.Add(Point(1, OtherSlot(), _nextX, _nextY));
        _handoff = true;
        _nextDownAt = nowMs;
        _handoffUntil = nowMs + _plannedOverlapMs;
        _shareRemainderX = _shareRemainderY = 0;
    }

    private (int X, int Y) Move(List<TouchBatchEvent> batch, int dx, int dy, double nowMs)
    {
        int oldX, oldY, nextX = 0, nextY = 0;
        if (_handoff)
        {
            var t = Math.Clamp((nowMs - _nextDownAt) / (_handoffUntil - _nextDownAt), 0, 1);
            var share = t * t * (3 - 2 * t);
            (oldX, nextX) = Split(dx, _x, _nextX, _minX, _maxX, share, ref _shareRemainderX);
            (oldY, nextY) = Split(dy, _y, _nextY, _minY, _maxY, share, ref _shareRemainderY);
        }
        else
        {
            oldX = Math.Clamp(_x + dx, _minX, _maxX) - _x;
            oldY = Math.Clamp(_y + dy, _minY, _maxY) - _y;
        }
        if (oldX != 0 || oldY != 0)
        {
            _x += oldX; _y += oldY;
            batch.Add(Point(2, ActiveSlot, _x, _y));
        }
        if (nextX != 0 || nextY != 0)
        {
            _nextX += nextX; _nextY += nextY;
            batch.Add(Point(2, OtherSlot(), _nextX, _nextY));
        }
        return (oldX + nextX, oldY + nextY);
    }

    private static (int Old, int Next) Split(int delta, int oldPosition, int nextPosition,
        int min, int max, double share, ref double remainder)
    {
        if (delta == 0) return (0, 0);
        var sign = Math.Sign(delta);
        var oldRoom = sign > 0 ? max - oldPosition : oldPosition - min;
        var nextRoom = sign > 0 ? max - nextPosition : nextPosition - min;
        var total = Math.Min(Math.Abs(delta), oldRoom + nextRoom);
        var ideal = total * share + remainder * sign;
        var rounded = (int)Math.Round(ideal);
        // Reallocate anything a contact cannot fit to the other contact. Only
        // quantization error is carried; a boundary must not create share debt.
        var next = Math.Clamp(rounded, Math.Max(0, total - oldRoom), Math.Min(total, nextRoom));
        remainder = next == rounded ? (ideal - next) * sign : 0;
        return ((total - next) * sign, next * sign);
    }

    private void CancelHandoff(List<TouchBatchEvent> batch, double nowMs)
    {
        // A reversal may exhaust the new contact first. Keep the older contact
        // with usable runway instead of promoting a finger already at its edge.
        batch.Add(Point(2, ActiveSlot, _x, _y));
        batch.Add(Point(2, OtherSlot(), _nextX, _nextY));
        batch.Add(new TouchBatchEvent(3, 0, 0, OtherSlot()));
        _handoff = false;
        _shareRemainderX = _shareRemainderY = 0;
        _spareReadyAt = nowMs + _settings.RelayOverlapMs * (.2 + .2 * Sample());
        PlanContact();
    }

    private void FinishHandoff(List<TouchBatchEvent> batch, double nowMs)
    {
        batch.Add(Point(2, ActiveSlot, _x, _y));
        batch.Add(Point(2, OtherSlot(), _nextX, _nextY));
        batch.Add(new TouchBatchEvent(3, 0, 0, ActiveSlot));
        ActiveSlot = OtherSlot();
        _x = _nextX; _y = _nextY;
        _handoff = false;
        _shareRemainderX = _shareRemainderY = 0;
        _activeDownAt = _nextDownAt;
        _spareReadyAt = nowMs + _settings.RelayOverlapMs * (.2 + .2 * Sample());
        PlanContact();
    }
}
