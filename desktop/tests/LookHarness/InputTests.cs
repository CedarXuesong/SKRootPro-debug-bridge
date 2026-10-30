using SkpdbDesktop;

internal static class InputTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!predicate()) await Task.Delay(5, deadline.Token);
    }

    public static async Task RunAsync()
    {
        var state = new SampledInputState();
        for (var i = 0; i < 20; ++i) { state.Set("F", true); state.Set("f", true); state.Set("F", false); }
        for (var i = 0; i < 40; ++i) { state.Advance(); Check(state.Pressed("f") == (i % 2 == 0), "Short press/repeat lost or duplicated"); }
        state.Set("MouseLeft", true); state.Advance(); state.Cancel(mouseOnly: true); state.Advance();
        Check(!state.Pressed("MouseLeft"), "Held mouse survived mode fence");
        state.Set("MouseLeft", true); state.Advance();
        Check(!state.Pressed("MouseLeft"), "Repeat revived a suppressed mouse");
        state.Set("MouseLeft", false); state.Set("MouseLeft", true); state.Advance();
        Check(state.Pressed("MouseLeft"), "Fresh mouse press was suppressed");
        var controls = new SampledInputState(canonicalKeys: false);
        controls.Set("Shift", true); controls.Advance();
        Check(controls.Pressed("shift") && !controls.Pressed("LeftShift"), "Control IDs were treated as keyboard aliases");
        Console.WriteLine("PASS sample edges preserve 20 fast clicks, deduplicate repeats and fence held input until UP");

        await using var peer = new ReconfigurationTests.Peer();
        using var control = new SkpdbClient();
        await control.ConnectAsync(peer.Options);
        await using var engine = new GamepadEngine();
        engine.ConfigureFastPath("127.0.0.1", 0, "", 2, 16);
        var device = new TouchDeviceInfo("input-test", "", "", 1280, 2772, 9, true, true);
        var profile = GamepadProfileStore.CreateDefaultProfile();
        profile.Move.Enabled = false;
        profile.Buttons = new()
        {
            Button("left", 2, "MouseLeft", GamepadControlKind.Fire),
            Button("right", 3, "MouseRight", GamepadControlKind.RightWalk),
            Button("middle", 4, "MouseMiddle", GamepadControlKind.Crosshair),
            Button("mode", 5, "Tab", GamepadControlKind.Look),
            Button("observeA", 6, "LeftAlt", GamepadControlKind.Observe),
            Button("observeB", 7, "B", GamepadControlKind.Observe),
            Button("key", 8, "F", GamepadControlKind.Tap)
        };
        await engine.StartAsync(control, device, profile);
        engine.AddMouseDelta(double.NaN, 0);
        engine.PointerTouchDown(double.NaN, 0);
        engine.PointerTouchMove(double.PositiveInfinity, 0);
        engine.PointerTouchMoveBy(0, double.NaN);
        engine.SetControlVector("key", double.NaN, 0);
        await Task.Delay(25);
        Check(engine.IsRunning && peer.ActiveCount == 0, "Nonfinite input polluted or stopped the sampler");
        profile.Buttons[0].Slot = 8; // Running mappings must own a detached snapshot.
        var mouse = new[] { "MouseLeft", "MouseRight", "MouseMiddle" };
        int Downs(int slot) => peer.Events.Count(e => e.Action == 1 && e.Slot == slot);
        foreach (var key in mouse) engine.SetMouseButton(key, true);
        engine.PointerTouchDown(.3, .4); engine.PointerTouchUp();
        await UntilAsync(() => peer.Events.Any(e => e.Action == 3 && e.Slot == 0));
        Check(Downs(0) == 1 && Downs(2) + Downs(3) + Downs(4) == 0, "Mouse mode leaked to gameplay bindings");
        engine.SetViewMode(true); await Task.Delay(35);
        Check(Downs(2) + Downs(3) + Downs(4) == 0, "Held mouse crossed into view mode");
        foreach (var key in mouse) engine.SetMouseButton(key, false);
        foreach (var key in mouse) engine.SetMouseButton(key, true);
        await UntilAsync(() => Downs(2) == 1 && Downs(3) == 1 && Downs(4) == 1);
        Check(peer.ActiveCount == 3, "Simultaneous view-mode buttons replaced each other");
        engine.SetViewMode(false);
        await UntilAsync(() => peer.ActiveCount == 0);
        engine.SetViewMode(true); await Task.Delay(35);
        Check(Downs(2) == 1 && Downs(3) == 1 && Downs(4) == 1, "Mode switch replayed held bindings");
        foreach (var key in mouse) engine.SetMouseButton(key, false);
        engine.SetMouseButton("MouseLeft", true);
        await UntilAsync(() => Downs(2) == 2);
        engine.SetMouseButton("MouseLeft", false);
        await UntilAsync(() => peer.ActiveCount == 0);
        Console.WriteLine("PASS mouse-mode direct touch isolation, view-mode simultaneous buttons, detached mappings and held-button mode switches");

        // Block an actual input ACK so every edge below arrives between samples.
        var entered = peer.BatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = peer.BatchProceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SetMouseButton("MouseRight", true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var taps = Downs(8);
        for (var i = 0; i < 12; ++i) { engine.SetKey("F", true); engine.SetKey("F", false); }
        proceed.TrySetResult();
        await UntilAsync(() => Downs(8) == taps + 12);
        engine.SetMouseButton("MouseRight", false);
        await UntilAsync(() => peer.ActiveCount == 0);
        engine.SetControlPressed("key", true); engine.SetControlPressed("key", false);
        await UntilAsync(() => Downs(8) == taps + 13);
        Console.WriteLine("PASS 12 down/up pairs queued during a stalled ACK and a short control press each produce exactly one action");

        engine.SetViewMode(false);
        engine.SetKey("LeftAlt", true); engine.SetKey("B", true);
        await UntilAsync(() => !engine.IsMouseMode);
        engine.SetKey("LeftAlt", false); await Task.Delay(35);
        Check(!engine.IsMouseMode, "One observe release cancelled another held observe key");
        engine.SetKey("B", false); await UntilAsync(() => engine.IsMouseMode);
        await UntilAsync(() => peer.ActiveCount == 0);
        Console.WriteLine("PASS multiple observe controls aggregate their holds and release only after the last UP");

        engine.SetViewMode(true); engine.SetMouseButton("MouseLeft", true);
        await UntilAsync(() => peer.ActiveCount > 0);
        engine.SetInputEnabled(false);
        await UntilAsync(() => peer.ActiveCount == 0);
        engine.SetInputEnabled(true); await Task.Delay(35);
        Check(engine.IsMouseMode && peer.ActiveCount == 0, "Focus restoration revived an old contact");
        engine.SetMouseButton("MouseLeft", false);
        var previous = Downs(8);
        engine.SetKey("F", true); engine.SetKey("F", false);
        await UntilAsync(() => Downs(8) == previous + 1);
        Console.WriteLine("PASS focus loss lifts every finger, clears modes and allows fresh input on return");

        var swipeProfile = GamepadProfileStore.CreateDefaultProfile();
        swipeProfile.Move.Enabled = false;
        swipeProfile.Buttons = new() { Button("swipe", 8, "F", GamepadControlKind.Swipe) };
        swipeProfile.Buttons[0].DurationMs = 180;
        await engine.ReconfigureAsync(device, swipeProfile);
        var swipeCount = Downs(8);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        engine.SetKey("F", true); engine.SetKey("F", false);
        await UntilAsync(() => Downs(8) > swipeCount);
        await Task.Delay(40);
        Check(peer.ActiveCount == 1 && engine.Metrics.ActiveSlots == 1, "Swipe duration collapsed into one batch");
        await UntilAsync(() => peer.ActiveCount == 0);
        Check(elapsed.ElapsedMilliseconds >= 160, "Configured swipe duration was ignored");
        swipeProfile.Buttons[0].Kind = GamepadControlKind.Fire;
        swipeProfile.Buttons[0].Mode = GamepadButtonMode.Rapid;
        swipeProfile.Buttons[0].TapIntervalMs = 30;
        await engine.ReconfigureAsync(device, swipeProfile);
        var rapidCount = Downs(8);
        engine.SetKey("F", true);
        await UntilAsync(() => Downs(8) >= rapidCount + 3);
        engine.SetKey("F", false); await Task.Delay(45);
        Check(peer.ActiveCount == 0 && engine.Metrics.ActiveSlots == 0, "Rapid mode was treated as a held finger");
        Console.WriteLine("PASS swipe runs for its configured duration; hold-control rapid mode repeats taps without phantom active slots");

        entered = peer.BatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        proceed = peer.BatchProceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PointerTouchDown(.2, .2);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var dragSlot = peer.Events.Last(e => e.Action == 1).Slot;
        for (var i = 0; i < 10000; ++i) engine.PointerTouchMove(.2 + .5 * i / 9999, .6);
        var queue = typeof(GamepadEngine).GetField("_pointerTouchQueue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
        Check((int)queue.GetType().GetProperty("Count")!.GetValue(queue)! == 1, "Pointer motion backlog grew without bounds");
        engine.PointerTouchUp(.7, .6); proceed.TrySetResult();
        await UntilAsync(() => peer.ActiveCount == 0);
        var finalMove = peer.Events.Last(e => e.Slot == dragSlot && e.Action == 2);
        var target = device.ToNative((int)Math.Round(.7 * (device.DisplayWidth - 1)), (int)Math.Round(.6 * (device.DisplayHeight - 1)));
        Check(finalMove.X == target.X && finalMove.Y == target.Y, "Coalescing lost the final release endpoint");
        Console.WriteLine("PASS 10,000 pointer moves coalesce during a blocked ACK and retain the exact UP endpoint");

        var invalid = GamepadProfileStore.CreateDefaultProfile(); invalid.Buttons[0].Slot = GamepadProfile.MaxLogicalSlot + 1;
        var connections = peer.Connections;
        try { await engine.StartAsync(control, device, invalid); throw new Exception("Invalid slot accepted"); }
        catch (InvalidOperationException) { }
        Check(engine.IsRunning && peer.Connections == connections, "Invalid start disrupted a working engine");
        await engine.StopAsync();
        using var cancel = new CancellationTokenSource();
        await engine.StartAsync(control, device, GamepadProfileStore.CreateDefaultProfile(), cancel.Token);
        engine.SetKey("W", true); await UntilAsync(() => peer.ActiveCount > 0);
        cancel.Cancel();
        await UntilAsync(() => !engine.IsRunning && peer.ActiveCount == 0);
        Check(await control.PingAsync() == "PONG", "Input cancellation closed shared control TCP");
        await engine.StartAsync(control, device, GamepadProfileStore.CreateDefaultProfile());
        Check(engine.IsRunning, "Engine could not restart after cancellation");
        Console.WriteLine("PASS invalid configuration preserves running engine; caller cancellation releases resources and permits restart");
        await RunFullLayoutTransitionsAsync(control, engine, peer, device);
    }

    private static async Task RunFullLayoutTransitionsAsync(SkpdbClient control, GamepadEngine engine,
        ReconfigurationTests.Peer peer, TouchDeviceInfo device)
    {
        // Same slot occupancy as the reported layout: move 0, look 1, eight
        // buttons 2..9, including a mode-only Tab key at 4.
        var full = GamepadProfileStore.CreateDefaultProfile();
        full.Look.Smoothing = 0;
        full.Look.RecenterMode = "never";
        full.Look.MaxRadiusX = .04;
        full.Buttons = Enumerable.Range(2, 8).Select(slot => Button("full" + slot, slot,
            slot switch { 2 => "MouseRight", 4 => "Tab", 5 => "MouseLeft", _ => "F" + slot },
            slot == 4 ? GamepadControlKind.Look : GamepadControlKind.Fire)).ToList();
        foreach (var rotation in new[] { 1, 3, 0, 2 })
        {
            var landscape = rotation is 1 or 3;
            var rotated = device.WithDisplay(rotation, landscape ? 2772 : 1280, landscape ? 1280 : 2772);
            await engine.ReconfigureAsync(rotated, full);
            Check(engine.DirectTouchAvailable, $"Full layout lost mouse clicks: rotation={rotation}, pointer=none");
            var relay = (LookTouchRelay)typeof(GamepadEngine).GetField("_lookRelay",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
            Check(relay.SpareSlot != full.Look.Slot, "Mode-only button blocked the second look finger");
            var endpoint = rotated.ToNative((int)Math.Round(.37 * (rotated.DisplayWidth - 1)),
                (int)Math.Round(.64 * (rotated.DisplayHeight - 1)));
            bool IsClick(TouchBatchEvent e) => e.Action == 1 && e.X == endpoint.X && e.Y == endpoint.Y;
            var start = peer.Events.Count;
            for (var i = 0; i < 8; i++)
            {
                engine.SetViewMode(true);
                await UntilAsync(() =>
                {
                    engine.AddMouseDelta(4, .1);
                    return relay.ActiveCount == 2;
                });
                engine.SetViewMode(false);
                var downs = peer.Events.Count(IsClick);
                engine.PointerTouchDown(.37, .64);
                await UntilAsync(() => peer.Events.Count(IsClick) > downs);
                Check(peer.Events.Last(IsClick) is var down &&
                    down.X == endpoint.X && down.Y == endpoint.Y, "Mouse click used the wrong rotation");
                if (i % 2 == 0)
                {
                    // Enter view while a direct drag is still held, before its
                    // normal 40 ms tap debounce. The old touch must lift first.
                    engine.SetViewMode(true);
                    engine.AddMouseDelta(60, 10);
                    engine.PointerTouchUp(); // Delayed page release from the old mode.
                    await UntilAsync(() => relay.IsDown);
                    engine.SetViewMode(false);
                }
                else engine.PointerTouchUp();
                await UntilAsync(() => peer.ActiveCount == 0);
            }
            var active = new HashSet<ushort>();
            foreach (var ev in peer.Events.Skip(start))
            {
                if (ev.Action == 1) Check(active.Add(ev.Slot), "Mode transition reused a still-held slot");
                else if (ev.Action == 3) active.Remove(ev.Slot);
            }
            Check(active.Count == 0, "Mode transition retained a touch");
        }
        foreach (var observe in new[] { false, true })
        {
            var mode = full.Buttons.Single(b => b.Slot == 4);
            mode.Kind = observe ? GamepadControlKind.Observe : GamepadControlKind.Fire;
            mode.Binding = observe ? "Tab" : "F4";
            await engine.ReconfigureAsync(device.WithDisplay(1, 2772, 1280), full);
            Check(engine.DirectTouchAvailable, "Full layout lost its dynamically allocated mouse contact");
            var relay = (LookTouchRelay)typeof(GamepadEngine).GetField("_lookRelay",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
            var start = peer.Events.Count;
            engine.PointerTouchDown(.3, .4);
            await UntilAsync(() => peer.ActiveCount == 1);
            if (observe) engine.SetKey("Tab", true);
            else { engine.SetViewMode(true); engine.AddMouseDelta(30, 5); }
            await UntilAsync(() => !engine.IsMouseMode && relay.IsDown);
            engine.PointerTouchUp();
            if (observe) engine.SetKey("Tab", false);
            else engine.SetViewMode(false);
            await UntilAsync(() => engine.IsMouseMode && peer.ActiveCount == 0);
            engine.PointerTouchDown(.7, .6); engine.PointerTouchUp();
            await UntilAsync(() => peer.Events.Skip(start).Count(e => e.Action == 1) >= 3 && peer.ActiveCount == 0);
            var active = new HashSet<ushort>();
            foreach (var ev in peer.Events.Skip(start))
            {
                if (ev.Action == 1) Check(active.Add(ev.Slot), "Dynamic primary/observe slot was reused before release");
                else if (ev.Action == 3) active.Remove(ev.Slot);
            }
            Check(active.Count == 0, "Dynamic primary/observe transition retained a touch");
        }
        Check(await control.PingAsync() == "PONG", "Mode transitions disrupted the main connection");
        Console.WriteLine("PASS full 10-slot layout: mouse clicks and two-finger look across 32 transitions, four rotations, held drags and late releases");
        Console.WriteLine("PASS dynamic primary/pointer leases and held-direct-touch transitions through observe mode");
    }

    private static GamepadButton Button(string id, int slot, string binding, GamepadControlKind kind) =>
        new() { Id = id, Slot = slot, Binding = binding, Kind = kind, X = .4, Y = .5 };
}
