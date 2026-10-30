using SkpdbDesktop;

internal static class JoystickTests
{
    private const double Step = 1000.0 / SampledJoystick.SampleRate;
    private static readonly TouchDeviceInfo Device = new("test", "", "", 1280, 2772, 9, true, true);
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static double Length(double x, double y) => Math.Sqrt(x * x + y * y);
    private static SampledJoystick Create(GamepadMoveSettings settings, int seed = 42, TouchDeviceInfo? device = null)
    { var stick = new SampledJoystick(new Random(seed)); stick.Configure(device ?? Device, settings); return stick; }
    public static void Run()
    {
        var settings = new GamepadMoveSettings { CenterX = .5, CenterY = .5, HoldSway = 0 };
        var starts = new HashSet<(int, int)>(); var ends = new HashSet<(int, int)>();
        var inward = false; var outward = false;
        for (var direction = 0; direction < 8; direction++)
        foreach (var rotation in new[] { 0, 1, 2, 3 })
        {
            var angle = direction * Math.PI / 4;
            var device = Device.WithDisplay(rotation, rotation % 2 == 0 ? 1280 : 2772, rotation % 2 == 0 ? 2772 : 1280);
            var stick = Create(settings, direction * 10 + rotation, device); var events = new List<TouchBatchEvent>();
            stick.Update(Math.Cos(angle), Math.Sin(angle), false, 0, events);
            Check(events.Count == 1 && events[0].Action == 1, "First sample must contain just DOWN");
            Check(Length(stick.Current.X, stick.Current.Y) > settings.Deadzone * .7, "First landing is still constrained to neutral");
            Check(stick.Current.X * Math.Cos(angle) + stick.Current.Y * Math.Sin(angle) > 0, "First landing points away from direction");
            Check(stick.PlannedSampleCount >= 10, "Path not pre-sampled"); starts.Add((events[0].X, events[0].Y));
            for (double t = Step; t < 350; t += Step) stick.Update(Math.Cos(angle), Math.Sin(angle), false, t, events);
            var p = stick.Current; var radius = Length(p.X, p.Y);
            Check(radius >= 1 - settings.TargetSpread - .001 && radius <= 1 + settings.TargetSpread + .001, "Radial random endpoint out of bounds");
            var angularError = Math.Atan2(Math.Sin(Math.Atan2(p.Y, p.X) - angle), Math.Cos(Math.Atan2(p.Y, p.X) - angle));
            Check(Math.Abs(angularError) <= settings.AngularSpread + 1e-8, "Angular random endpoint out of bounds");
            inward |= radius < 1; outward |= radius > 1; ends.Add((events[^1].X, events[^1].Y));
            var logical = device.ToLogical(events[^1].X, events[^1].Y);
            Check(Math.Abs(Length(logical.X - .5 * (device.DisplayWidth - 1), logical.Y - .5 * (device.DisplayHeight - 1)) - radius * stick.RadiusPixels) < 2, "Circular geometry or rotation mismatch");
            var endpoint = stick.Current; events.Clear(); stick.Update(0, 0, false, 351, events);
            Check(!stick.IsDown && events.Count == 2 && events[^1].Action == 3, "Release did not lift immediately");
            Check(stick.Current.X == endpoint.X && stick.Current.Y == endpoint.Y, "Release moved toward center");
            events.Clear(); stick.Update(0, 0, false, 1000, events); Check(events.Count == 0, "Motion replayed after lift");
        }
        Check(inward && outward && starts.Count >= 25 && ends.Count >= 25, "Insufficient directional/radial random coverage");
        Console.WriteLine("PASS 8 directions x 4 rotations: off-center directional DOWN, inward/outward endpoints, immediate in-place UP");

        var turnsSettings = settings.Clone(); turnsSettings.TargetSpread = turnsSettings.AngularSpread = turnsSettings.Distortion = 0;
        bool innerBow = false, outerBow = false;
        for (var seed = 0; seed < 40; seed++)
        {
            var stick = Create(turnsSettings, seed); var events = new List<TouchBatchEvent>();
            stick.Update(1, 0, false, 0, events); stick.Update(1, 0, false, 300, events);
            stick.Update(0, -1, false, 300, events);
            double minRadius = 2, maxBow = 0;
            for (double t = 300 + Step; t < 550; t += Step)
            {
                stick.Update(0, -1, false, t, events); var p = stick.Current;
                minRadius = Math.Min(minRadius, Length(p.X, p.Y));
                var bow = (p.X - p.Y - 1) / Math.Sqrt(2);
                maxBow = Math.Max(maxBow, Math.Abs(bow)); innerBow |= bow < -.006; outerBow |= bow > .006;
            }
            Check(minRadius < .80 && minRadius > .55, $"Turn follows circumference or excessive bow: {minRadius}");
            Check(maxBow < turnsSettings.Curvature * .46 + .001, "Turn deviates excessively from direct chord");
        }
        Check(innerBow && outerBow, "Random curve did not bend both inward and outward");
        Console.WriteLine("PASS 40 direct chord turns with bounded random inward/outward curvature");

        var interrupted = Create(settings); var batch = new List<TouchBatchEvent>();
        interrupted.Update(1, 0, false, 0, batch);
        var inputs = new[] { (1d, 0d), (0d, -1d), (-1d, 0d), (1d, 1d), (0d, 1d) };
        var previous = inputs[0]; double now = 0;
        for (var i = 1; i <= 500; i++)
        {
            now += 9 + i % 7; interrupted.Update(previous.Item1, previous.Item2, i % 3 == 0, now, batch);
            var before = interrupted.Current; var next = inputs[i % inputs.Length];
            interrupted.Update(next.Item1, next.Item2, i % 3 == 0, now, batch); var after = interrupted.Current;
            Check(Length(before.X - after.X, before.Y - after.Y) < 1e-9 && Length(before.VelocityX - after.VelocityX, before.VelocityY - after.VelocityY) < 1e-9 && Length(before.AccelerationX - after.AccelerationX, before.AccelerationY - after.AccelerationY) < 1e-9, $"Retarget changed state at {i}: {before} to {after}"); previous = next;
        }
        Check(batch.Count(e => e.Action == 1) == 1 && batch.All(e => e.Action != 3), "Retarget changed contact identity");
        Console.WriteLine("PASS 500 rapid interruptions preserve position/velocity/acceleration and contact identity");

        var swaySettings = settings.Clone(); swaySettings.HoldSway = .04;
        var sway = Create(swaySettings); batch.Clear(); sway.Update(1, 0, false, 0, batch);
        var points = new List<JoystickSample>();
        for (double t = Step; t < 5000; t += Step) { sway.Update(1, 0, false, t, batch); if (t > 350) points.Add(sway.Current); }
        Check(points.Max(p => p.Y) - points.Min(p => p.Y) > .02, "Held endpoint remains stationary");
        Check(points.Max(p => p.Y) - points.Min(p => p.Y) <= swaySettings.HoldSway * 2.01, "Sway escaped endpoint envelope");
        Check(points.Zip(points.Skip(1), (a, b) => Length(a.X - b.X, a.Y - b.Y)).Max() < .002, "Held sway has packet jitter");
        var sprintSettings = turnsSettings.Clone(); sprintSettings.SprintExtra = .72;
        var sprint = Create(sprintSettings); batch.Clear(); sprint.Update(1, 0, false, 0, batch); sprint.Update(1, 0, false, 400, batch);
        var beforeSprint = sprint.Current; sprint.Update(1, 0, true, 400, batch); Check(sprint.Current == beforeSprint, "Shift jumped position");
        sprint.Update(1, 0, true, 900, batch); Check(Math.Abs(Length(sprint.Current.X, sprint.Current.Y) - 1.72) < 1e-7, "Sprint ignores edited extra radius");
        Console.WriteLine("PASS smooth bounded held sway and editable sprint circle with continuous Shift transition");

        foreach (var response in new[] { 40, 300 })
        foreach (var extreme in new[] { false, true })
        {
            var cfg = settings.Clone(); cfg.ResponseMs = response;
            if (extreme) { cfg.StartSpread = .95; cfg.TargetSpread = .2; cfg.AngularSpread = .35; cfg.Curvature = .4; cfg.Distortion = .15; cfg.SprintExtra = 1; cfg.HoldSway = .08; }
            var stick = Create(cfg, 902); var limit = 1 + cfg.SprintExtra + cfg.TargetSpread + cfg.HoldSway + .12;
            for (var i = 0; i < 20000; i++)
            {
                var angle = Math.Floor(i / 3d) * 2.7; var on = i % 29 >= 2; batch.Clear();
                var wasDown = stick.IsDown; var last = stick.Current;
                stick.Update(on ? Math.Cos(angle) : 0, on ? Math.Sin(angle) : 0, i % 11 < 5, i * Step, batch);
                var p = stick.Current;
                Check(double.IsFinite(p.X + p.Y + p.VelocityX + p.VelocityY + p.AccelerationX + p.AccelerationY) && Length(p.X, p.Y) <= limit + .001, $"Stress escaped bound: {response}/{extreme}/{i}: {Length(p.X,p.Y)}");
                if (wasDown && stick.IsDown) Check(Length(p.X - last.X, p.Y - last.Y) < .65, $"Large uninterrupted sample jump at {i}");
                Check(batch.All(e => e.X >= 0 && e.X < Device.ScreenWidth && e.Y >= 0 && e.Y < Device.ScreenHeight), "Native sample out of bounds");
            }
        }
        Console.WriteLine("PASS 80,000 stress frames at default and maximum parameter values");
        TestAlternativesAndButtons();
    }
    private static void TestAlternativesAndButtons()
    {
        var profile = GamepadProfileStore.CreateDefaultProfile(); profile.Buttons.Clear();
        for (var i = 0; i < 3; i++) { var m = profile.Move.Clone(); m.CenterX += .13 * (i + 1); profile.ExtraMoves.Add(m); var l = profile.Look.Clone(); l.AnchorX -= .1 * (i + 1); profile.ExtraLooks.Add(l); }
        profile.Validate(); var saved = GamepadProfileStore.DeserializeProfile(GamepadProfileStore.SerializeProfile(profile.Clone()))!; saved.Validate();
        Check(saved.ExtraMoves.Count == 3 && saved.ExtraLooks.Count == 3 && saved.ExtraMoves[2].CenterX == profile.ExtraMoves[2].CenterX, "Alternative components did not persist");
        var preview = new LocalInputPreview(Device, saved, new Random(71)); var moves = new HashSet<int>(); var looks = new HashSet<int>();
        double t = 0;
        for (var i = 0; i < 180; i++)
        {
            preview.Step(t, 1, 0, false, 1, 0, true); var mi = preview.ActiveMoveIndex; var li = preview.ActiveLookIndex;
            moves.Add(mi); looks.Add(li);
            for (var j = 1; j < 10; j++) { preview.Step(t + j * Step, 0, -1, true, 1, 0, true); Check(preview.ActiveMoveIndex == mi && preview.ActiveLookIndex == li, "Component changed during a gesture"); }
            preview.Step(t + 10 * Step, 0, 0, false, 0, 0, releaseLook: true); t += 12 * Step;
        }
        Check(moves.Count == 4 && looks.Count == 4, "Random selection never reached some alternatives");
        saved.Move.Enabled = false; saved.Look.Enabled = false;
        var rng = new Random(12); Check(Enumerable.Range(0, 100).All(_ => saved.ChooseMove(rng).Enabled && saved.ChooseLook(rng).Enabled), "Disabled primary selected over enabled alternatives");
        Console.WriteLine("PASS multiple components: clone/JSON, all 4 choices reached, fixed per gesture, disabled candidates skipped");

        var button = new GamepadButton { Id = "fire", Binding = "F", Kind = GamepadControlKind.Fire, Slot = 2, X = .5, Y = .5, Radius = .08 };
        var contact = new SampledButtonContact(new Random(23)); var points = new HashSet<(int, int)>();
        foreach (var rotation in new[] { 0, 1, 2, 3 })
        {
            var device = Device.WithDisplay(rotation, rotation % 2 == 0 ? 1280 : 2772, rotation % 2 == 0 ? 2772 : 1280);
            for (var i = 0; i < 200; i++)
            {
                var events = new List<TouchBatchEvent>(); contact.Press(device, button, events); var held = contact.HeldTouch!.Value;
                var p = device.ToLogical(held.X, held.Y); points.Add((p.X, p.Y));
                Check(Length(p.X - .5 * (device.DisplayWidth - 1), p.Y - .5 * (device.DisplayHeight - 1)) <= .08 * 1280 + 2, "Button landed outside component circle");
                contact.Press(device, button, events); Check(events.Count == 1 && contact.HeldTouch == held, "Held button re-randomized");
                contact.Release(events); Check(events[^2] == held && events[^1].Action == 3 && contact.HeldTouch is null, "Button lift moved point or retained contact");
            }
        }
        Check(points.Count > 700, "Button landings lack variability");
        saved.Buttons.Add(button); var buttonPreview = new LocalInputPreview(Device, saved, new Random(8));
        var pressed = new HashSet<string> { "F" }; buttonPreview.Step(0, 0, 0, false, 0, 0, pressedButtons: pressed);
        var first = buttonPreview.Trace.Drain().Single();
        buttonPreview.Step(Step, 0, 0, false, 0, 0, pressedButtons: pressed); var next = buttonPreview.Trace.Drain().Single();
        Check(next.X == first.X && next.Y == first.Y && next.Action == 2, "Offline held button differs from landing");
        buttonPreview.ReleaseAll(); Check(buttonPreview.Trace.Drain().Last().Action == 3, "Offline focus release lost button lift");
        Console.WriteLine("PASS 800 random button landings across rotations, held-point stability, offline button trace and release");
    }
}

