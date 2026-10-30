using SkpdbDesktop;

internal static class MicroLookTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static (long X, long Y) Motion(IEnumerable<TouchBatchEvent> events, TouchDeviceInfo device)
    {
        var fingers = new Dictionary<ushort, (int X, int Y)>();
        long x = 0, y = 0;
        foreach (var ev in events)
        {
            var point = device.ToLogical(ev.X, ev.Y);
            if (ev.Action == 1) fingers.Add(ev.Slot, point);
            else if (ev.Action == 2)
            { var old = fingers[ev.Slot]; x += point.X - old.X; y += point.Y - old.Y; fingers[ev.Slot] = point; }
            else fingers.Remove(ev.Slot);
        }
        return (x, y);
    }

    public static async Task RunAsync()
    {
        foreach (var rotation in Enumerable.Range(0, 4))
        foreach (var smooth in new[] { 0.0, .95 })
        foreach (var direction in new[] { -1, 1 })
        {
            var device = new TouchDeviceInfo("micro", "", "", 1280, 2772, 9, true, true)
                .WithDisplay(rotation, rotation % 2 == 0 ? 1280 : 2772, rotation % 2 == 0 ? 2772 : 1280);
            var profile = GamepadProfileStore.CreateDefaultProfile(); profile.Move.Enabled = false;
            profile.Look.Sensitivity = .1; profile.Look.Smoothing = smooth;
            profile.Look.RecenterDelayMs = 40;
            profile.ExtraLooks.Add(profile.Look.Clone()); profile.ExtraLooks[0].AnchorX = .4;
            var preview = new LocalInputPreview(device, profile, new Random(31));
            var events = new List<TouchBatchEvent>();
            // Each event is only 0.1 display pixels; gaps also exercise idle lifting.
            for (var i = 0; i < 9000; i++)
            {
                var dx = i % 30 == 0 ? direction : 0;
                preview.Step(i * 4, 0, 0, false, dx, -dx, false);
                events.AddRange(preview.Trace.Drain().Select(p =>
                { var n = device.ToNative((int)p.X, (int)p.Y); return new TouchBatchEvent(p.Action, n.X, n.Y, p.Slot); }));
            }
            preview.ReleaseAll();
            events.AddRange(preview.Trace.Drain().Select(p =>
            { var n = device.ToNative((int)p.X, (int)p.Y); return new TouchBatchEvent(p.Action, n.X, n.Y, p.Slot); }));
            var moved = Motion(events, device);
            Check(Math.Abs(moved.X - 30 * direction) < 1.01 && Math.Abs(moved.Y + 30 * direction) < 1.01,
                $"Sparse subpixel motion lost: rotation={rotation}, smooth={smooth}, motion={moved}");
            Check(!preview.Look.HasGesture && preview.Slots.ActiveCount == 0, "Explicit release retained gesture state");
        }
        Console.WriteLine("PASS sparse subpixel motion and idle carry across all rotations, both directions, zero/high smoothing and alternative regions");

        await using var peer = new ReconfigurationTests.Peer();
        using var client = new SkpdbClient(); await client.ConnectAsync(peer.Options);
        await using var engine = new GamepadEngine();
        engine.ConfigureFastPath("127.0.0.1", 0, "", 2, 16);
        var d = new TouchDeviceInfo("micro-wire", "", "", 1280, 2772, 9, true, true);
        var p = GamepadProfileStore.CreateDefaultProfile(); p.Move.Enabled = false;
        p.Look.Sensitivity = .1; p.Look.Smoothing = .95;
        await engine.StartAsync(client, d, p); engine.SetViewMode(true);
        for (var i = 0; i < 40; i++) { engine.AddMouseDelta(1, -1); await Task.Delay(18); }
        await Task.Delay(100); await engine.StopAsync();
        var movedWire = Motion(peer.Events, d);
        Check(Math.Abs(movedWire.X - 4) <= 1 && Math.Abs(movedWire.Y + 4) <= 1,
            "Production engine discarded first-touch fractional packets: " + movedWire);
        Check(peer.ActiveCount == 0 && await client.PingAsync() == "PONG", "Micro motion test retained fingers or broke primary");
        Console.WriteLine("PASS real engine/TCP output preserves 40 single-count events at low sensitivity with high smoothing");
    }
}
