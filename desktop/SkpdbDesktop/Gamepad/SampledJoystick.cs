namespace SkpdbDesktop;

public readonly record struct JoystickSample(double X, double Y, double VelocityX, double VelocityY,
    double AccelerationX, double AccelerationY);

/// <summary>240 Hz sampled Cartesian gestures, preserving C2 continuity on every retarget.</summary>
public sealed class SampledJoystick(Random? random = null)
{
    public const int SampleRate = 240;
    private readonly Random _random = random ?? Random.Shared;
    private TouchDeviceInfo _device = null!;
    private GamepadMoveSettings _settings = null!;
    private double _centerX, _centerY, _radius, _limit;
    private JoystickSample _state;
    private MotionPlan? _plan;
    private double _requestedAngle, _requestedPower, _lastTime, _targetX, _targetY;
    private int _nativeX, _nativeY;
    public bool IsDown { get; private set; }
    public int PlannedSampleCount => _plan?.Samples.Length ?? 0;
    public double RadiusPixels => _radius;
    public JoystickSample Current => _state;
    public GamepadMoveSettings Settings => _settings;
    public TouchBatchEvent? HeldTouch => IsDown ? new(2, _nativeX, _nativeY, (ushort)_settings.Slot) : null;

    public void Configure(TouchDeviceInfo device, GamepadMoveSettings settings)
    {
        if (IsDown) throw new InvalidOperationException("切换轮盘配置前必须释放旧触点");
        if (settings.Slot < 0 || settings.Slot > device.SlotMax)
            throw new InvalidOperationException("轮盘 slot 超出触摸屏支持范围");
        _device = device; _settings = settings;
        _centerX = Math.Clamp(settings.CenterX * (device.DisplayWidth - 1), 0, device.DisplayWidth - 1);
        _centerY = Math.Clamp(settings.CenterY * (device.DisplayHeight - 1), 0, device.DisplayHeight - 1);
        _limit = MotionLimit(settings);
        _radius = FittedRadius(settings, device.DisplayWidth, device.DisplayHeight);
        _state = default; _plan = null; _requestedPower = _lastTime = 0;
    }

    private static double MotionLimit(GamepadMoveSettings settings) =>
        1 + settings.SprintExtra + settings.TargetSpread + settings.HoldSway + .12;
    public static double FittedRadius(GamepadMoveSettings settings, double width, double height)
    {
        var cx = settings.CenterX * (width - 1); var cy = settings.CenterY * (height - 1);
        var room = Math.Max(0, Math.Min(Math.Min(cx, width - 1 - cx), Math.Min(cy, height - 1 - cy)));
        return Math.Max(0, Math.Min(settings.Radius * Math.Min(width, height), room / MotionLimit(settings)));
    }

    public void Update(double x, double y, bool sprint, double nowMs, List<TouchBatchEvent> batch)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(nowMs))
            throw new ArgumentOutOfRangeException(nameof(x), "轮盘输入必须为有限数值");
        nowMs = Math.Max(nowMs, _lastTime); _lastTime = nowMs;
        if (!_settings.Enabled || _radius < 1) { Release(batch); return; }
        var length = Math.Sqrt(x * x + y * y);
        var engaged = length > Math.Max(.0001, _settings.Deadzone);
        if (!engaged) { Release(batch); return; } // Lift exactly where the last sample was emitted.
        var angle = Math.Atan2(y, x);
        if (_settings.Snap8) angle = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        var power = Math.Min(1, length) * (sprint ? 1 + _settings.SprintExtra : 1);
        if (!IsDown)
        {
            var startAngle = angle + Signed() * _settings.AngularSpread;
            var startRadius = _settings.StartSpread * (.15 + .85 * _random.NextDouble());
            _state = new(startRadius * Math.Cos(startAngle), startRadius * Math.Sin(startAngle), 0, 0, 0, 0);
            _requestedAngle = angle; _requestedPower = power;
            IsDown = true; PlanTarget(angle, power, nowMs); Emit(1, batch); return;
        }
        Advance(nowMs);
        if (Math.Abs(Wrap(angle - _requestedAngle)) > Math.PI / 360 || Math.Abs(power - _requestedPower) > .02)
        {
            _requestedAngle = angle; _requestedPower = power; PlanTarget(angle, power, nowMs);
        }
        Emit(2, batch);
    }

    public void Release(List<TouchBatchEvent> batch)
    {
        if (!IsDown) return;
        Emit(2, batch); batch.Add(new(3, 0, 0, (ushort)_settings.Slot));
        IsDown = false; _plan = null;
        _state = _state with { VelocityX = 0, VelocityY = 0, AccelerationX = 0, AccelerationY = 0 };
    }
    private double Signed() => _random.NextDouble() * 2 - 1;
    private static double Wrap(double angle) => Math.Atan2(Math.Sin(angle), Math.Cos(angle));
    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);

    private void PlanTarget(double angle, double power, double nowMs)
    {
        var targetAngle = angle + Signed() * _settings.AngularSpread;
        var radius = Math.Max(_settings.Deadzone + .015, power + Signed() * _settings.TargetSpread);
        _targetX = Math.Cos(targetAngle) * radius; _targetY = Math.Sin(targetAngle) * radius;
        var distance = Length(_targetX - _state.X, _targetY - _state.Y);
        var duration = _settings.ResponseMs * (.85 + .25 * _random.NextDouble()) * Math.Clamp(.7 + distance * .4, .7, 1.8);
        Plan(_targetX, _targetY, duration, nowMs, _settings.Curvature * Math.Min(1, distance), _settings.Distortion);
    }
    private void Plan(double x, double y, double duration, double nowMs, double curve, double distortion)
    {
        var dx = x - _state.X; var dy = y - _state.Y; var distance = Math.Max(.0001, Length(dx, dy));
        var bow = Signed() * curve * .45; var twist = Signed() * distortion * .35;
        for (var attempt = 0; ; attempt++)
        {
            var plan = new MotionPlan(_state, x, y, Math.Max(4, duration), nowMs, -dy / distance * bow,
                dx / distance * bow, -dy / distance * twist, dx / distance * twist);
            if (plan.Samples.All(s => Length(s.X, s.Y) <= _limit) || attempt >= 24)
            { _plan = plan; break; }
            // Reduce braking distance instead of clipping velocity at an interruption.
            duration *= .75; bow *= .75; twist *= .75;
        }
    }
    private void Advance(double nowMs)
    {
        if (_plan is null) return;
        if (nowMs > _plan.EndTime && _settings.HoldSway > 0)
        {
            var endTime = _plan.EndTime; _state = _plan.Samples[^1];
            // Alternating targets on either side of the settled endpoint make a
            // slow, bounded hand sway. Each segment starts and ends at rest.
            var phase = (_swayDirection = -_swayDirection);
            var angle = _requestedAngle + Math.PI / 2 + Signed() * .45;
            var amount = _settings.HoldSway * (.45 + .55 * _random.NextDouble());
            Plan(_targetX + phase * Math.Cos(angle) * amount,
                _targetY + phase * Math.Sin(angle) * amount, 450 + _random.NextDouble() * 650,
                endTime, _settings.HoldSway * .15, 0);
            // Large caller time jumps still settle to a finite current segment.
            if (nowMs > _plan!.EndTime) { _state = _plan.Samples[^1]; return; }
        }
        _state = _plan.At(nowMs);
    }
    private int _swayDirection = 1;
    private void Emit(byte action, List<TouchBatchEvent> batch)
    {
        var native = _device.ToNative((int)Math.Round(_centerX + _state.X * _radius),
            (int)Math.Round(_centerY + _state.Y * _radius));
        _nativeX = native.X; _nativeY = native.Y;
        batch.Add(new(action, native.X, native.Y, (ushort)_settings.Slot));
    }

    private sealed class MotionPlan
    {
        public readonly JoystickSample[] Samples;
        public readonly double EndTime;
        private readonly double _start, _step;
        public MotionPlan(JoystickSample start, double x, double y, double duration, double time,
            double bowX, double bowY, double twistX, double twistY)
        {
            _start = time; EndTime = time + duration;
            var count = Math.Max(2, (int)Math.Ceiling(duration * SampleRate / 1000) + 1);
            _step = duration / (count - 1); Samples = new JoystickSample[count];
            var cx = Coefficients(start.X, start.VelocityX, start.AccelerationX, x, 0, 0, duration);
            var cy = Coefficients(start.Y, start.VelocityY, start.AccelerationY, y, 0, 0, duration);
            AddBump(cx, bowX, twistX); AddBump(cy, bowY, twistY);
            for (var i = 0; i < count; i++)
            {
                var u = (double)i / (count - 1);
                var sx = Evaluate(cx, u, duration); var sy = Evaluate(cy, u, duration);
                Samples[i] = new(sx.P, sy.P, sx.V, sy.V, sx.A, sy.A);
            }
            Samples[0] = start; Samples[^1] = new(x, y, 0, 0, 0, 0);
        }
        public JoystickSample At(double time)
        {
            var index = Math.Max(0, (time - _start) / _step);
            if (index >= Samples.Length - 1) return Samples[^1];
            var i = (int)index; var u = index - i; var left = Samples[i]; var right = Samples[i + 1];
            if (u == 0) return left;
            var x = Interpolate(left.X, left.VelocityX, left.AccelerationX, right.X, right.VelocityX, right.AccelerationX, _step, u);
            var y = Interpolate(left.Y, left.VelocityY, left.AccelerationY, right.Y, right.VelocityY, right.AccelerationY, _step, u);
            return new(x.P, y.P, x.V, y.V, x.A, y.A);
        }
        private static double[] Coefficients(double p0, double v0, double a0, double p1,
            double v1, double a1, double t)
        {
            var c = new double[8];
            Fill(c, p0, v0, a0, p1, v1, a1, t);
            return c;
        }

        private static void Fill(Span<double> c, double p0, double v0, double a0,
            double p1, double v1, double a1, double t)
        {
            c[0] = p0; c[1] = v0 * t; c[2] = a0 * t * t / 2;
            var d = p1 - c[0] - c[1] - c[2];
            var v = v1 * t - c[1] - 2 * c[2];
            var a = a1 * t * t - 2 * c[2];
            c[3] = 10 * d - 4 * v + a / 2;
            c[4] = -15 * d + 7 * v - a;
            c[5] = 6 * d - 3 * v + a / 2;
        }

        private static void AddBump(double[] c, double bow, double twist)
        {
            ReadOnlySpan<double> bump = [0, 0, 0, 64, -192, 192, -64];
            for (var i = 3; i < bump.Length; i++)
            {
                c[i] += bump[i] * (bow - twist);
                c[i + 1] += bump[i] * 2 * twist;
            }
        }

        private static (double P, double V, double A) Interpolate(double p0, double v0, double a0,
            double p1, double v1, double a1, double t, double u)
        {
            Span<double> c = stackalloc double[6];
            Fill(c, p0, v0, a0, p1, v1, a1, t);
            return Evaluate(c, u, t);
        }

        private static (double P, double V, double A) Evaluate(ReadOnlySpan<double> c, double u, double t)
        {
            double p = c[^1], v = 0, a = 0;
            for (var i = c.Length - 2; i >= 0; i--) { a = a * u + 2 * v; v = v * u + p; p = p * u + c[i]; }
            return (p, v / t, a / (t * t));
        }
    }
}
