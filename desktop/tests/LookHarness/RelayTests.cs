using SkpdbDesktop;

internal static class RelayTests
{
    private sealed class Camera
    {
        private readonly TouchDeviceInfo _device;
        public Dictionary<ushort, (int X, int Y)> Fingers { get; } = new();
        public long X, Y;
        public int Downs, MaxFingers, LargestMove;

        public Camera(TouchDeviceInfo? device = null) => _device = device ?? Device;

        public void Apply(IEnumerable<TouchBatchEvent> batch)
        {
            foreach (var e in batch)
            {
                var point = _device.ToLogical(e.X, e.Y);
                if (e.Action == 1)
                {
                    Check(Fingers.TryAdd(e.Slot, point), "duplicate down on live slot");
                    Downs++;
                }
                else if (e.Action == 2)
                {
                    Check(Fingers.TryGetValue(e.Slot, out var old), "move on released slot");
                    var dx = point.X - old.X; var dy = point.Y - old.Y;
                    LargestMove = Math.Max(LargestMove, Math.Max(Math.Abs(dx), Math.Abs(dy)));
                    // Games accepting simultaneous look contacts add every
                    // finger's delta, just like multiple relative mice.
                    X += dx; Y += dy;
                    Fingers[e.Slot] = point;
                }
                else
                {
                    Check(Fingers.Remove(e.Slot), "duplicate up on released slot");
                }
                MaxFingers = Math.Max(MaxFingers, Fingers.Count);
                Check(e.Action == 3 || e.X >= 0 && e.X < _device.ScreenWidth &&
                    e.Y >= 0 && e.Y < _device.ScreenHeight,
                    "touch left physical display");
            }
        }
    }

    private static readonly TouchDeviceInfo Device = new("test", "", "", 2772, 1280, 9, true, true);

    public static void Run()
    {
        Scenario("continuous right", _ => (4.0, 0.0));
        Scenario("continuous left", _ => (-4.0, 0.0));
        Scenario("diagonal and reversals", i => i % 240 < 120 ? (6.0, 3.0) : (-6.0, -3.0));
        Scenario("continuous diagonal", _ => (4.0, 3.0));
        Scenario("near screen edge", _ => (4.0, 0.0), p => p.Look.AnchorX = 0.99);
        Scenario("reverse from screen edge", _ => (-4.0, 0.0), p => p.Look.AnchorX = 0.99);
        Scenario("large batched input", i => i % 100 == 0 ? (180.0, -60.0) : (0.0, 0.0));
        Scenario("filter tail and fractional motion", i => i < 200 ? (0.11, 0.07) : (0.0, 0.0), p => p.Look.Smoothing = .95);
        Scenario("fractional motion across handoffs", _ => (1.11, .37), p => p.Look.Smoothing = .95);
        Scenario("vertical inversion", _ => (1.0, 3.0), p => p.Look.InvertY = true);
        Scenario("no spare slot", _ => (4.0, 0.0), p =>
        {
            p.Buttons = Enumerable.Range(2, 8).Select(s => new GamepadButton { Slot = s }).ToList();
        });
        Scenario("single hardware slot", _ => (4.0, 0.0), p =>
        {
            p.Move.Enabled = false; p.Look.Slot = 0;
        }, Device with { SlotMax = 0 });

        var profile = new GamepadProfile();
        profile.Move.Enabled = false;
        var relay = new LookTouchRelay();
        relay.Configure(Device with { SlotMax = 1 }, profile);
        Check(relay.SpareSlot == 0, "spare must respect hardware slot maximum and disabled movement");
        Console.WriteLine("PASS hardware slot bounds");

        profile = new GamepadProfile();
        relay.Configure(Device, profile);
        var camera = new Camera();
        double now = 0;
        while (relay.ActiveCount < 2 && now < 2000)
        {
            var batch = new List<TouchBatchEvent>();
            relay.Update(4, 0, now += 4, true, false, false, batch);
            camera.Apply(batch);
        }
        Check(relay.ActiveCount == 2, "handoff was not reached");
        Check(relay.HeldTouches().Select(t => t.Slot).Distinct().Count() == 2,
            "held snapshot must include both relay fingers");
        var release = new List<TouchBatchEvent>();
        relay.Update(100, 100, now + 4, false, false, true, release);
        Check(release.All(e => e.Action != 1) && release.Count(e => e.Action == 3) == 2,
            "mode switch must flush and release both fingers without starting another drag");
        camera.Apply(release);
        Check(camera.Fingers.Count == 0 && relay.ActiveCount == 0, "handoff release left a finger down");
        Console.WriteLine("PASS release during overlap, including same-tick mouse input");

        relay.Configure(Device, profile);
        camera = new Camera();
        var hold = new List<TouchBatchEvent>();
        relay.Update(0, 0, 0, false, true, false, hold);
        camera.Apply(hold);
        Check(camera.Fingers.Count == 1, "observe must touch down before motion");
        var idle = new List<TouchBatchEvent>();
        relay.Update(1, 0, 4, false, true, false, idle); camera.Apply(idle);
        for (var i = 1; i <= 50; i++)
        {
            idle.Clear(); relay.Update(0, 0, 4 + i * 4, false, false, false, idle); camera.Apply(idle);
        }
        Check(camera.Fingers.Count == 0, "idle filter tail prevented release");
        Console.WriteLine("PASS observe hold and idle tail release");
        NaturalTimeline();
        NaturalIdleAndGestures();
        NaturalSettings();
        AdditiveSamples();
        ShareRamp();
        Console.WriteLine("All relay regression scenarios passed.");
    }

    private static void Scenario(string name, Func<int, (double X, double Y)> input,
        Action<GamepadProfile>? edit = null, TouchDeviceInfo? device = null)
    {
        var profile = new GamepadProfile();
        profile.Look.RecenterMode = "never";
        edit?.Invoke(profile);
        var relay = new LookTouchRelay(new Random(234));
        relay.Configure(device ?? Device, profile);
        var camera = new Camera(device);
        double expectedX = 0, expectedY = 0, now = 0;
        for (var i = 0; i < 1000; i++)
        {
            var (x, y) = input(i);
            expectedX += x * profile.Look.Sensitivity;
            expectedY += y * profile.Look.Sensitivity * (profile.Look.InvertY ? -1 : 1);
            var batch = new List<TouchBatchEvent>();
            relay.Update(x, y, now += 4, true, false, false, batch);
            camera.Apply(batch);
        }
        for (var i = 0; i < 1000; i++)
        {
            var batch = new List<TouchBatchEvent>();
            relay.Update(0, 0, now += 4, true, false, false, batch);
            camera.Apply(batch);
        }
        Check(Math.Abs(camera.X - expectedX) < 1.01 && Math.Abs(camera.Y - expectedY) < 1.01,
            $"{name}: lost movement, got {camera.X},{camera.Y}, expected {expectedX:F2},{expectedY:F2}");
        Check(camera.LargestMove <= 24, $"{name}: unsmoothed burst {camera.LargestMove}");
        Check(camera.MaxFingers <= 2, $"{name}: more than two relay fingers");
        var release = new List<TouchBatchEvent>(); relay.Release(release); camera.Apply(release);
        Check(camera.Fingers.Count == 0, $"{name}: stop left touches down");
        Console.WriteLine($"PASS {name} ({camera.Downs} drags, displacement {camera.X},{camera.Y})");
    }

    private static void NaturalTimeline()
    {
        var profile = new GamepadProfile();
        profile.Look.RecenterMode = "never";
        var relay = new LookTouchRelay(new Random(321));
        relay.Configure(Device, profile);
        var camera = new Camera();
        var starts = new List<(ushort Slot, int X, int Y)>();
        var ends = new List<int>();
        var overlaps = new List<double>();
        double nextDown = 0, now = 0;
        for (var i = 0; i < 5100; i++)
        {
            var batch = new List<TouchBatchEvent>();
            relay.Update(i < 5000 ? 1 : 0, 0, now += 4, true, false, false, batch);
            foreach (var e in batch)
            {
                if (e.Action == 1)
                {
                    if (camera.Fingers.Count == 1) nextDown = now;
                    starts.Add((e.Slot, e.X, e.Y));
                }
                if (e.Action == 3 && camera.Fingers.Count == 2)
                {
                    overlaps.Add(now - nextDown);
                    ends.Add(camera.Fingers[e.Slot].X);
                }
                if (e.Action != 3)
                {
                    var minX = (int)Math.Ceiling((profile.Look.AnchorX - profile.Look.MaxRadiusX) * (Device.DisplayWidth - 1));
                    var maxX = (int)Math.Floor((profile.Look.AnchorX + profile.Look.MaxRadiusX) * (Device.DisplayWidth - 1));
                    Check(e.X >= minX && e.X <= maxX, "random contact left configured look region");
                }
                camera.Apply(new[] { e });
            }
            Check(camera.Fingers.Count >= 1, "relay introduced an all-fingers-up gap");
        }
        Check(overlaps.Count >= 6 && overlaps.All(ms => ms >= 128 && ms <= 196),
            "slow continuous motion did not retain the requested overlap");
        Check(overlaps.Distinct().Count() >= 4, "overlap durations did not vary");
        Check(starts.Select(p => (p.X, p.Y)).Distinct().Count() >= 6, "contact starts did not vary");
        Check(ends.Distinct().Count() >= 4 && ends.All(x => x < 2690), "contacts ended at a fixed hard boundary");
        Check(starts.Select(p => p.Slot).SequenceEqual(starts.Select((_, i) => (ushort)(i % 2 == 0 ? 1 : 9))),
            "relay slots did not alternate");
        Check(Math.Abs(camera.X - 8000) < 1.01 && camera.Y == 0, "random placement changed camera displacement");
        var release = new List<TouchBatchEvent>(); relay.Release(release); camera.Apply(release);
        Check(camera.Fingers.Count == 0, "natural relay stop left touches down");
        Console.WriteLine($"PASS natural relay: overlap {overlaps.Min():F0}..{overlaps.Max():F0} ms, " +
            $"{starts.Count} alternating contacts, varied starts/endpoints, exact displacement");
    }

    private static void NaturalIdleAndGestures()
    {
        var profile = new GamepadProfile();
        var relay = new LookTouchRelay(new Random(456));
        relay.Configure(Device, profile);
        var camera = new Camera();
        double now = 0;
        while (relay.ActiveCount < 2 && now < 4000)
        {
            var batch = new List<TouchBatchEvent>();
            relay.Update(2, 0, now += 4, true, false, false, batch); camera.Apply(batch);
        }
        Check(relay.ActiveCount == 2, "idle overlap test never reached relay");
        var began = now;
        for (var i = 0; i < 120; i++)
        {
            var batch = new List<TouchBatchEvent>();
            relay.Update(0, 0, now += 4, true, false, false, batch);
            if (now - began < 128) Check(batch.All(e => e.Action != 3), "idle ended the second contact too early");
            camera.Apply(batch);
        }
        Check(camera.Fingers.Count == 0, "natural idle retained touches indefinitely");
        relay.Configure(Device, profile);
        camera = new Camera();
        var slots = new List<ushort>();
        for (var gesture = 0; gesture < 4; gesture++)
        {
            for (var i = 0; i < 150; i++)
            {
                var batch = new List<TouchBatchEvent>();
                relay.Update(i < 20 ? 1 : 0, 0, now += 4, true, false, false, batch);
                slots.AddRange(batch.Where(e => e.Action == 1).Select(e => e.Slot));
                camera.Apply(batch);
            }
            Check(camera.Fingers.Count == 0, "short drag did not release after idle");
        }
        Check(slots.SequenceEqual(new ushort[] { 1, 9, 1, 9 }), "short gestures did not alternate slots");
        Console.WriteLine("PASS natural idle dwell and alternating short gestures");
    }

    private static void NaturalSettings()
    {
        var profile = new GamepadProfile();
        profile.Look.RelayOverlapMs = 240;
        profile.Look.RelayStartJitter = .18;
        profile.Look.RelayEarlyEnd = .22;
        var copy = GamepadProfileStore.DeserializeProfile(GamepadProfileStore.SerializeProfile(profile.Clone()))!;
        Check(copy.Look.RelayOverlapMs == 240 && copy.Look.RelayStartJitter == .18 && copy.Look.RelayEarlyEnd == .22,
            "relay settings were lost by undo/copy or persistence");
        copy.Look.RelayOverlapMs = 3000; copy.Look.RelayStartJitter = -1; copy.Look.RelayEarlyEnd = 2;
        copy.Validate();
        Check(copy.Look.RelayOverlapMs == 600 && copy.Look.RelayStartJitter == 0 && copy.Look.RelayEarlyEnd == .30,
            "relay settings were not bounded");
        Console.WriteLine("PASS relay settings persistence and validation");
    }

    private static void AdditiveSamples()
    {
        var frames = 0; var cancellations = 0; var handoffs = 0;
        foreach (var rotation in new[] { 0, 1, 2, 3 })
        foreach (var overlap in new[] { 40, 160, 600 })
        foreach (var region in new[] { "normal", "small", "screen edge" })
        {
            var device = new TouchDeviceInfo("additive", "", "", 1280, 2772, 9, true, true)
                .WithDisplay(rotation, rotation % 2 == 0 ? 1280 : 2772, rotation % 2 == 0 ? 2772 : 1280);
            var profile = new GamepadProfile();
            var look = profile.Look;
            look.Sensitivity = 1; look.Smoothing = 0; look.RecenterMode = "never";
            look.RelayOverlapMs = overlap; look.RelayStartJitter = .25; look.RelayEarlyEnd = .30;
            if (region == "small") look.MaxRadiusX = look.MaxRadiusY = .01;
            if (region == "screen edge") look.AnchorX = .99;
            var relay = new LookTouchRelay(new Random(921)); relay.Configure(device, profile);
            var camera = new Camera(device);
            var random = new Random(728);
            var order = new Dictionary<ushort, int>();
            double now = 0;
            var intervals = new[] { 1.0, 4, 1000.0 / 240, 8, 16, 33, 100 };
            var scale = region == "small" ? 1 : 4;
            var label = $"rotation {rotation}, overlap {overlap}, {region}";
            for (var i = 0; i < 2400; i++)
            {
                var phase = i % 600;
                var (dx, dy) = phase switch
                {
                    < 120 => (5, 2), < 240 => (-5, -2), < 360 => (5, -2), < 480 => (-5, 2),
                    < 540 => (random.Next(-5, 6), random.Next(-3, 4)), _ => (0, 0)
                };
                dx *= scale; dy *= scale;
                var before = (camera.X, camera.Y);
                var batch = new List<TouchBatchEvent>();
                relay.Update(dx, dy, now += intervals[i % intervals.Length], true, false, false, batch);
                foreach (var e in batch)
                {
                    if (e.Action == 1) order[e.Slot] = ++handoffs;
                    if (e.Action == 2)
                    {
                        var old = camera.Fingers[e.Slot]; var point = device.ToLogical(e.X, e.Y);
                        Check(dx == 0 ? point.X == old.X : (point.X - old.X) * Math.Sign(dx) >= 0,
                            $"{label}: a contact moved against the requested X direction");
                        Check(dy == 0 ? point.Y == old.Y : (point.Y - old.Y) * Math.Sign(dy) >= 0,
                            $"{label}: a contact moved against the requested Y direction");
                    }
                    if (e.Action == 3 && camera.Fingers.Count == 2 &&
                        order[e.Slot] > order[camera.Fingers.Keys.Single(slot => slot != e.Slot)])
                        cancellations++;
                    camera.Apply([e]);
                    Check(camera.Fingers.Count is >= 1 and <= 2, $"{label}: gap or extra live contact");
                    if (e.Action != 3)
                    {
                        var point = device.ToLogical(e.X, e.Y);
                        Check(point.X >= Math.Ceiling(Math.Max(0, look.AnchorX - look.MaxRadiusX) * (device.DisplayWidth - 1)) &&
                            point.X <= Math.Floor(Math.Min(1, look.AnchorX + look.MaxRadiusX) * (device.DisplayWidth - 1)) &&
                            point.Y >= Math.Ceiling(Math.Max(0, look.AnchorY - look.MaxRadiusY) * (device.DisplayHeight - 1)) &&
                            point.Y <= Math.Floor(Math.Min(1, look.AnchorY + look.MaxRadiusY) * (device.DisplayHeight - 1)),
                            $"{label}: contact left its configured region");
                    }
                }
                Check(camera.X - before.X == dx && camera.Y - before.Y == dy,
                    $"{label}, frame {i}: additive delta {camera.X - before.X},{camera.Y - before.Y}, requested {dx},{dy}");
                var held = relay.HeldTouches().ToDictionary(e => e.Slot, e => device.ToLogical(e.X, e.Y));
                Check(held.Count == camera.Fingers.Count && held.All(p => camera.Fingers[p.Key] == p.Value),
                    $"{label}: held snapshot differs from emitted positions");
                frames++;
            }
            var end = (camera.X, camera.Y);
            var release = new List<TouchBatchEvent>(); relay.Release(release); camera.Apply(release);
            Check(camera.Fingers.Count == 0 && end == (camera.X, camera.Y), $"{label}: release moved camera or left a finger");
        }
        Check(cancellations > 0, "sample stress never exercised reversal cancellation");
        Console.WriteLine($"PASS additive multi-touch: {frames:N0} exact per-sample deltas in 36 cases, " +
            $"{handoffs:N0} contacts, {cancellations:N0} reversal cancellations, no doubled speed, gaps or out-of-bounds touches");
    }

    private static void ShareRamp()
    {
        var profile = new GamepadProfile();
        profile.Look.Sensitivity = 1; profile.Look.Smoothing = 0;
        profile.Look.RelayEarlyEnd = .30;
        var relay = new LookTouchRelay(new Random(37)); relay.Configure(Device, profile);
        var camera = new Camera();
        var shares = new List<int>();
        ushort? oldSlot = null, newSlot = null;
        double now = 0;
        var completed = false;
        for (var i = 0; i < 2000 && !completed; i++)
        {
            var batch = new List<TouchBatchEvent>();
            var before = camera.X;
            relay.Update(12, 0, now += 4, true, false, false, batch);
            var newDelta = 0;
            foreach (var e in batch)
            {
                if (e.Action == 1 && camera.Fingers.Count == 1 && oldSlot is null)
                { oldSlot = camera.Fingers.Keys.Single(); newSlot = e.Slot; }
                if (e.Action == 2 && e.Slot == newSlot) newDelta += e.X - camera.Fingers[e.Slot].X;
                if (e.Action == 3 && e.Slot == oldSlot) completed = true;
                camera.Apply([e]);
            }
            Check(camera.X - before == 12 && camera.Y == 0, "share ramp changed aggregate speed");
            if (oldSlot is not null) shares.Add(newDelta);
        }
        Check(completed && shares.Count >= 32, "share ramp ended before the planned overlap");
        Check(shares[0] == 0 && shares[^1] >= 11, "ownership did not ease from old to new finger");
        Check(shares.Distinct().Count() >= 8 && shares.All(delta => delta is >= 0 and <= 12),
            "share ramp used a fixed half-speed split or overshot");
        Check(shares.Take(shares.Count / 4).Average() < 2 && shares.TakeLast(shares.Count / 4).Average() > 10,
            "overlap did not progressively transfer ownership");
        Check(shares.Zip(shares.Skip(1), (a, b) => Math.Abs(b - a)).All(change => change <= 3),
            "per-contact speed jumped during overlap");
        var next = new List<TouchBatchEvent>(); var previous = camera.X;
        relay.Update(12, 0, now + 4, true, false, false, next); camera.Apply(next);
        Check(camera.Fingers.Count == 1 && camera.Fingers.ContainsKey(newSlot!.Value) && camera.X - previous == 12,
            "new finger did not retain full speed after old finger lifted");
        var release = new List<TouchBatchEvent>(); relay.Release(release); camera.Apply(release);
        Console.WriteLine($"PASS smooth additive handoff: {shares.Count} overlap samples, new-finger delta 0→12, constant camera speed 12 px/sample");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
