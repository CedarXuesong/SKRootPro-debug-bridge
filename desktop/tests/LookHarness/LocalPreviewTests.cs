using SkpdbDesktop;

internal static class LocalPreviewTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        const double step = 1000.0 / SampledJoystick.SampleRate;
        foreach (var rotation in new[] { 0, 1, 2, 3 })
        {
            var device = new TouchDeviceInfo("offline", "", "", 1280, 2772, 9, true, true)
                .WithDisplay(rotation, rotation % 2 == 0 ? 1280 : 2772, rotation % 2 == 0 ? 2772 : 1280);
            var profile = GamepadProfileStore.CreateDefaultProfile();
            var preview = new LocalInputPreview(device, profile, new Random(17));
            var random = new Random(17);
            var actualMove = new SampledJoystick(random); actualMove.Configure(device, profile.Move);
            var actualLook = new LookTouchRelay(random); actualLook.Configure(device, profile);
            var active = new HashSet<ushort>();
            var overlap = false;
            var received = 0;
            double lastTime = -1;
            for (var i = 0; i < 3000; i++)
            {
                var t = i * step;
                var angle = Math.Floor(i / 13.0) * 1.7;
                var engaged = i % 79 > 8;
                var x = engaged ? Math.Cos(angle) : 0; var y = engaged ? Math.Sin(angle) : 0;
                var dx = 2.8 * Math.Cos(t / 850); var dy = 1.5 * Math.Sin(t / 600);
                var expected = new List<TouchBatchEvent>();
                actualMove.Update(x, y, i % 101 < 40, t, expected);
                actualLook.Update(dx, dy, t, true, true, false, expected);
                preview.Step(t, x, y, i % 101 < 40, dx, dy, true);
                var points = preview.Trace.Drain();
                Check(points.Length == expected.Count, "Offline event count differs from production samplers");
                for (var n = 0; n < points.Length; n++)
                {
                    var p = points[n]; var e = expected[n]; var logical = device.ToLogical(e.X, e.Y);
                    Check(p.Action == e.Action && p.Slot == e.Slot && p.X == logical.X && p.Y == logical.Y,
                        "Offline preview fabricated/interpolated an output coordinate");
                    Check(p.TimeMs == t && p.TimeMs >= lastTime, "Preview sample timestamp is not monotonic");
                    Check(p.Width == device.DisplayWidth && p.Height == device.DisplayHeight &&
                        p.X >= 0 && p.X < p.Width && p.Y >= 0 && p.Y < p.Height, "Rotated trace left screen bounds");
                    if (p.Action == 1) Check(active.Add(p.Slot), "Duplicate DOWN on an occupied slot");
                    if (p.Action == 3) Check(active.Remove(p.Slot), "UP without a preceding DOWN");
                    lastTime = p.TimeMs; received++;
                }
                overlap |= preview.Look.ActiveCount == 2;
            }
            preview.ReleaseAll();
            foreach (var p in preview.Trace.Drain())
                if (p.Action == 3) Check(active.Remove(p.Slot), "Stop emitted an unmatched UP");
            Check(active.Count == 0 && !preview.Move.IsDown && !preview.Look.IsDown, "Closing preview left held contacts");
            Check(overlap && received > 3000, "Combined preview did not exercise simultaneous move/look/handoff");
            Check(preview.Samples == 3000, "Preview sample counter drifted");
            preview.Reset();
            Check(preview.Samples == 0 && preview.Trace.Drain().Length == 0, "Replay retained an old sample/contact");
            preview.Step(0, 1, 0, false, 0, 0);
            Check(preview.Trace.Drain().Single().Action == 1, "Replay did not start a new contact");
        }
        Console.WriteLine("PASS offline dual preview: 12,000 frames exactly match production samplers across all rotations, two-finger handoff, balanced release and replay");

        var queue = new TouchTraceBuffer { Enabled = true };
        var screen = new TouchDeviceInfo("test", "", "", 1280, 2772, 9, true, true);
        for (var i = 0; i < 20000; i++) queue.Record(screen, [new(2, i % 1280, 2, 0)], i);
        var bounded = queue.Drain();
        Check(bounded.Length == 8192 && bounded[0].TimeMs == 20000 - 8192, "Diagnostics queue is not bounded/newest-first retained");
        queue.Record(screen, [new(1, 1, 1, 0)]); queue.Enabled = false;
        Check(queue.Drain().Length == 0, "Disabling live tracing kept stale input");
        Console.WriteLine("PASS live trace queue cap and disable/clear behavior");
    }
}
