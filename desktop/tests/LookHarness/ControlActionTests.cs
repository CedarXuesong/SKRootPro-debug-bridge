using SkpdbDesktop;

internal static class ControlActionTests
{
    private static readonly TouchDeviceInfo Screen = new("test", "", "", 1280, 2772, 9, true, true);
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
    private static GamepadButton Button(GamepadControlKind kind) => new()
    { Id = "control", Binding = "F", Kind = kind, Slot = 2, X = .5, Y = .5, DirectionX = .6, DirectionY = -.8, DurationMs = 120 };

    public static void Run()
    {
        TestCoordinates();
        foreach (var kind in new[] { GamepadControlKind.Tap, GamepadControlKind.Skill })
        {
            var action = new SampledButtonAction(Screen, Button(kind), new(), new Random(17));
            var batch = new List<TouchBatchEvent>();
            action.Update(true, 0, batch);
            Check(batch.Count == 1 && batch[0].Action == 1 && action.IsDown, "Tap did not start with a single DOWN");
            batch.Clear(); action.Update(false, 39, batch);
            Check(action.IsDown && batch.Count == 0, "Tap lifted before its hold duration");
            action.Update(false, 40, batch);
            Check(!action.IsDown && batch.Count == 2 && batch[^1].Action == 3, "Tap did not lift in a later sample at 40ms");

            // Preserve 12 rapid edges while the first contact is still active.
            for (var i = 0; i < 12; i++)
            { action.Update(true, 100 + i * 2, batch); action.Update(false, 101 + i * 2, batch); }
            var downs = batch.Count(e => e.Action == 1); var ups = batch.Count(e => e.Action == 3) - 1;
            for (var t = 125; t <= 1000; t += 5)
            {
                batch.Clear(); action.Update(false, t, batch);
                Check(!(batch.Any(e => e.Action == 1) && batch.Any(e => e.Action == 3)), "Queued tap folded DOWN and UP into one sample");
                downs += batch.Count(e => e.Action == 1); ups += batch.Count(e => e.Action == 3);
            }
            Check(downs == 12 && ups == 12 && !action.IsDown && action.PendingCount == 0, "Queued taps lost or duplicated an edge");
        }
        Console.WriteLine("PASS Tap/Skill hold 40ms across separate batches; 12 queued short presses execute exactly once");
        foreach (var kind in new[] { GamepadControlKind.Tap, GamepadControlKind.Skill })
        {
            var action = new SampledButtonAction(Screen, Button(kind), new(), new Random(17));
            var batch = new List<TouchBatchEvent>();
            action.Update(true, 0, batch); var held = action.HeldTouch;
            for (var time = 40; time <= 2000; time += 40)
            {
                batch.Clear(); action.Update(true, time, batch);
                Check(action.IsDown && action.HeldTouch == held && batch.Count == 0, "Normal key lifted while physically held");
            }
            action.Update(false, 2001, batch);
            Check(!action.IsDown && batch.Count(e => e.Action == 3) == 1, "Normal key did not lift on release");
            Check(GamepadControlCatalog.Info(kind).UsesButtonMode, "Normal key mode is hidden in the editor");
        }
        Console.WriteLine("PASS normal Tap/Skill keep one stable randomized contact for two seconds and lift only on release");
        foreach (var kind in new[] { GamepadControlKind.Tap, GamepadControlKind.Skill })
        {
            var action = new SampledButtonAction(Screen, Button(kind), new(), new Random(17));
            var batch = new List<TouchBatchEvent>();
            action.Update(true, 0, batch); action.Update(false, 5, batch);
            action.Update(true, 10, batch); action.Update(false, 15, batch);
            action.Update(true, 20, batch);
            for (var t = 25; t <= 2000; t += 5) action.Update(true, t, batch);
            Check(batch.Count(e => e.Action == 1) == 3 && batch.Count(e => e.Action == 3) == 2 && action.IsDown,
                "Queued short taps swallowed or prolonged the latest held press");
            action.Update(false, 2001, batch);
            Check(!action.IsDown && batch.Count(e => e.Action == 3) == 3, "Latest queued held press stuck");
        }

        foreach (var rotation in new[] { 0, 1, 2, 3 })
        foreach (var kind in new[] { GamepadControlKind.Swipe, GamepadControlKind.Scroll, GamepadControlKind.Zoom })
        foreach (var direction in new[] { -1, 1 })
        {
            var screen = Screen.WithDisplay(rotation, rotation % 2 == 0 ? 1280 : 2772, rotation % 2 == 0 ? 2772 : 1280);
            var button = Button(kind);
            var action = new SampledButtonAction(screen, button, new(), new Random(6));
            var batch = new List<TouchBatchEvent>();
            if (kind == GamepadControlKind.Swipe) action.Update(true, 0, batch);
            else { action.Pulse(direction); action.Update(false, 0, batch); }
            var start = screen.ToLogical(batch.Single().X, batch.Single().Y);
            batch.Clear(); action.Update(false, 60, batch);
            Check(action.IsDown && batch.All(e => e.Action == 2), "Timed gesture collapsed or lifted early");
            var half = screen.ToLogical(action.HeldTouch!.Value.X, action.HeldTouch.Value.Y);
            batch.Clear(); action.Update(false, 120, batch);
            var end = screen.ToLogical(batch[^2].X, batch[^2].Y);
            var targetX = kind == GamepadControlKind.Swipe ? (int)Math.Round(start.X + button.DirectionX * button.Distance * (screen.DisplayWidth - 1)) : start.X;
            var targetY = (int)Math.Round(start.Y + (kind == GamepadControlKind.Swipe ? button.DirectionY * button.Distance : -direction * button.Amount) * (screen.DisplayHeight - 1));
            Check(end == (targetX, targetY) && batch[^1].Action == 3 && !action.IsDown, "Gesture missed its configured endpoint/duration");
            Check(Math.Abs(half.X - (start.X + end.X) / 2d) <= 1 && Math.Abs(half.Y - (start.Y + end.Y) / 2d) <= 1,
                "Gesture did not traverse the midpoint smoothly");
        }
        Console.WriteLine("PASS Swipe/Scroll/Zoom preserve duration, midpoint, endpoint and both pulse directions across four rotations");

        foreach (var kind in new[] { GamepadControlKind.Tap, GamepadControlKind.Skill, GamepadControlKind.Fire, GamepadControlKind.RightWalk, GamepadControlKind.Crosshair })
        foreach (var mode in new[] { GamepadButtonMode.Hold, GamepadButtonMode.Toggle })
        {
            var button = Button(kind); button.Mode = mode;
            var action = new SampledButtonAction(Screen, button, new(), new Random(16)); var batch = new List<TouchBatchEvent>();
            action.Update(true, 0, batch); var held = action.HeldTouch;
            batch.Clear(); action.Update(true, 100, batch);
            Check(action.HeldTouch == held && batch.Count == 0, "Held point moved or re-randomized");
            action.Update(false, 110, batch);
            if (mode == GamepadButtonMode.Toggle)
            { Check(action.IsDown, "Toggle lifted on physical release"); action.Update(true, 120, batch); }
            Check(!action.IsDown && batch.Count(e => e.Action == 3) == 1, "Hold/toggle did not release exactly once");
        }
        foreach (var kind in new[] { GamepadControlKind.Rapid, GamepadControlKind.Tap, GamepadControlKind.Skill, GamepadControlKind.Fire })
        foreach (var interval in new[] { 16, 30, 80, 2000 })
        {
            var button = Button(kind); button.Mode = GamepadButtonMode.Rapid; button.TapIntervalMs = interval;
            var action = new SampledButtonAction(Screen, button, new(), new Random(19)); var batch = new List<TouchBatchEvent>();
            var downs = new List<int>(); var ups = new List<int>();
            for (var t = 0; t < 5000; t += 2)
            {
                batch.Clear(); action.Update(t < 4400, t, batch);
                if (batch.Any(e => e.Action == 1)) downs.Add(t);
                if (batch.Any(e => e.Action == 3)) ups.Add(t);
                Check(action.PendingCount == 0 && !(batch.Any(e => e.Action == 1) && batch.Any(e => e.Action == 3)), "Rapid queued replay or folded edges");
            }
            Check(downs.Count == ups.Count && downs.Count >= 3 && !action.IsDown, "Rapid stopped with a held contact");
            for (var i = 0; i < downs.Count; i++) Check(ups[i] - downs[i] >= Math.Clamp(interval / 2d, 8, 40), "Rapid press was too short");
            for (var i = 1; i < downs.Count; i++) Check(downs[i] - downs[i - 1] >= interval, "Rapid exceeded configured rate");
        }
        Console.WriteLine("PASS hold/toggle point stability and balanced release; Rapid honors 16/30/80/2000ms without replay backlog");
        TestPreviewParity();
    }

    private static void TestCoordinates()
    {
        for (var rotation = 0; rotation < 4; rotation++)
        {
            var width = rotation % 2 == 0 ? 1280 : 2772; var height = rotation % 2 == 0 ? 2772 : 1280;
            var screen = Screen.WithDisplay(rotation, width, height);
            var corners = new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) };
            var nativeCorners = corners.Select(p => screen.ToNative(p.Item1, p.Item2)).ToHashSet();
            Check(nativeCorners.SetEquals(new[] { (0, 0), (1279, 0), (0, 2771), (1279, 2771) }), "Rotation lost a native screen edge");
            foreach (var p in corners.Concat(Enumerable.Range(0, 10000).Select(i => (i * 71 % width, i * 131 % height))))
            { var n = screen.ToNative(p.Item1, p.Item2); Check(screen.ToLogical(n.X, n.Y) == p, "Rotation roundtrip drifted"); }
            screen = Screen.WithDisplay(rotation, width / 2, height / 2);
            foreach (var p in new[] { (0, 0), (width / 2 - 1, height / 2 - 1), (width / 4, height / 4) })
            { var n = screen.ToNative(p.Item1, p.Item2); Check(screen.ToLogical(n.X, n.Y) == p, "Resized display did not scale to native pixels"); }
        }
        Console.WriteLine("PASS 40,016 rotation roundtrips, all screen corners and scaled logical display coordinates");
    }

    private static void TestPreviewParity()
    {
        var cases = Enum.GetValues<GamepadControlKind>().Where(k => k is not (GamepadControlKind.Look or GamepadControlKind.Observe));
        foreach (var kind in cases)
        foreach (var mode in Enum.GetValues<GamepadButtonMode>())
        {
            var profile = GamepadProfileStore.CreateDefaultProfile(); profile.Move.Enabled = profile.Look.Enabled = false;
            var button = Button(kind); button.Mode = mode; profile.Buttons = new() { button }; profile.Validate();
            var preview = new LocalInputPreview(Screen, profile, new Random(27));
            var action = new SampledButtonAction(Screen, button, profile.Move, new Random(27));
            var batch = new List<TouchBatchEvent>(); var down = new HashSet<string> { button.Binding }; var empty = new HashSet<string>();
            for (var i = 0; i < 1000; i++)
            {
                var pressed = i % 53 < 19; var t = i * 5d;
                batch.Clear(); action.Update(pressed, t, batch);
                if (action.HeldTouch is { } held && !batch.Any(e => e.Slot == held.Slot)) batch.Add(held);
                preview.Step(t, 0, 0, false, 0, 0, pressedButtons: pressed ? down : empty);
                var trace = preview.Trace.Drain();
                Check(trace.Length == batch.Count, $"Preview event count differs: {kind}/{mode}/{i}");
                for (var n = 0; n < trace.Length; n++)
                { var e = batch[n]; var p = Screen.ToLogical(e.X, e.Y); Check(trace[n].Action == e.Action && trace[n].Slot == e.Slot && trace[n].X == p.X && trace[n].Y == p.Y, "Preview action/position diverged from production"); }
            }
            batch.Clear(); action.Release(batch); preview.ReleaseAll();
            Check(!action.IsDown && action.PendingCount == 0 && preview.Trace.Drain().Count(p => p.Action == 3) == batch.Count(e => e.Action == 3), "Reset lost contact cleanup");
            preview.Reset(); preview.Step(0, 0, 0, false, 0, 0);
            Check(preview.Trace.Drain().Length == 0, "Reset replayed pending control input");
        }
        var queued = new SampledButtonAction(Screen, Button(GamepadControlKind.Zoom), new(), new Random(2));
        for (var i = 0; i < 10000; i++) queued.Pulse(1);
        Check(queued.PendingCount == 64, "Wheel pulse queue is not bounded");
        queued.Release(new()); Check(queued.PendingCount == 0 && !queued.IsDown, "Release did not discard queued gestures");
        Console.WriteLine("PASS every non-mode control and mode matches production in the canvas; release/reset clears held input and bounded pending gestures");
    }
}
