using SkpdbDesktop;

internal static class DynamicSlotTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
    private static TouchBatchEvent Down(ushort id, int y = 0) => new(1, id, y, id);
    private static TouchBatchEvent Move(ushort id, int y) => new(2, id, y, id);
    private static TouchBatchEvent Up(ushort id) => new(3, 0, 0, id);
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    public static GamepadProfile LargeProfile(int count)
    {
        var profile = GamepadProfileStore.CreateDefaultProfile();
        profile.Move.Slot = 65000; profile.Look.Slot = 65001;
        profile.Look.Smoothing = 0; profile.Look.RecenterMode = "never";
        profile.Look.MaxRadiusX = .04;
        profile.Buttons = Enumerable.Range(0, count).Select(i => new GamepadButton
        {
            Id = "many" + i, Binding = "Key" + i, Slot = 1000 + i,
            Kind = GamepadControlKind.Fire, Radius = .01,
            X = .05 + i % 10 * .09, Y = .05 + i / 10 * .09
        }).ToList();
        profile.Validate();
        return profile;
    }

    public static async Task RunAsync()
    {
        TestLeases();
        TestFuzz();
        TestProfilesAndPreview();
        TestLookDisplacement();
        await TestEngine();
    }

    private static void TestLeases()
    {
        var slots = new TouchSlotAllocator(1);
        var initial = slots.Process([Down(100), Down(101), Down(102), Down(103)]);
        Check(initial.Count == 2 && slots.ActiveCount == 2 && slots.PendingCount == 2, "Capacity was exceeded");
        var first = slots.GetPhysicalSlot(100)!.Value;
        var second = slots.GetPhysicalSlot(101)!.Value;
        var update = slots.Process([Move(101, 22), Move(102, 33), Up(103)]);
        Check(update.Single() == Move(101, 22) with { Slot = second } && slots.PendingCount == 1,
            "Waiting contact leaked to output or held lease moved");
        var refill = slots.Process([Up(100)]);
        Check(refill.SequenceEqual(new[] { Up(first), Down(102, 33) with { Slot = first } }),
            "FIFO refill did not follow release with the newest waiting position");
        Check(slots.GetPhysicalSlot(101) == second, "An existing touch was reassigned");
        slots.Process([Down(104), Down(105)]);
        var cancelled = slots.Process([Up(101), Up(104), Up(105)]);
        Check(cancelled.Count == 1 && slots.PendingCount == 0 && slots.ActiveCount == 1,
            "A released waiting gesture became a ghost click");
        // Do not collapse old MOVE/UP/new DOWN in a sample: relative-look
        // movement depends on preserving the final position of the old finger.
        var ordered = slots.Process([Move(102, 40), Up(102), Down(102, 50), Move(102, 60)]);
        Check(ordered.Select(e => e.Action).SequenceEqual(new byte[] { 2, 3, 1, 2 }) && ordered[0].Y == 40,
            "Handoff endpoints/order were collapsed");
        slots.Process([Up(102)]);
        Check(slots.ReleaseAll(force: true).Select(e => e.Slot).Order().SequenceEqual(new ushort[] { first, second }.Order()),
            "Emergency cleanup forgot slots after an uncertain UP");
        Check(slots.ActiveCount == 0 && slots.PendingCount == 0 && slots.Process([Move(102, 9), Up(103)]).Count == 0,
            "Cleanup retained or resurrected contacts");
        Console.WriteLine("PASS dynamic leases: stable ownership, latest position, FIFO, cancelled waits, ordered handoff and emergency cleanup");
    }

    private static void TestFuzz()
    {
        foreach (var capacity in new[] { 1, 2, 3, 10 })
        {
            var allocator = new TouchSlotAllocator(capacity - 1);
            var desired = new HashSet<ushort>();
            var physical = new Dictionary<ushort, int>();
            var stable = new Dictionary<ushort, ushort>();
            var random = new Random(711 + capacity);
            void Verify(IEnumerable<TouchBatchEvent> output)
            {
                foreach (var e in output)
                {
                    Check(e.Slot < capacity, "Physical slot escaped device capacity");
                    if (e.Action == 1)
                    {
                        Check(physical.TryAdd(e.Slot, e.X) && desired.Contains((ushort)e.X), "Duplicate or phantom DOWN");
                        Check(!physical.Values.GroupBy(x => x).Any(g => g.Count() > 1), "Logical contact acquired two physical slots");
                    }
                    if (e.Action == 2) Check(physical.GetValueOrDefault(e.Slot, -1) == e.X, "MOVE changed contact owner");
                    if (e.Action == 3) Check(physical.Remove(e.Slot), "Unmatched physical UP");
                }
                Check(physical.Count == Math.Min(capacity, desired.Count) && physical.Count == allocator.ActiveCount &&
                    allocator.PendingCount == desired.Count - physical.Count, "Occupancy/pending accounting drifted");
                foreach (var id in desired)
                    if (allocator.GetPhysicalSlot(id) is { } slot)
                    {
                        Check(!stable.TryGetValue(id, out var old) || slot == old, "Held contact changed lease");
                        Check(physical[slot] == id, "Lease owner differs from output owner");
                        stable[id] = slot;
                    }
            }
            for (var tick = 0; tick < 25000; tick++)
            {
                var id = (ushort)random.Next(10, 74);
                if (!desired.Contains(id)) { desired.Add(id); Verify(allocator.Process([Down(id, tick)])); }
                else if (random.Next(3) == 0)
                { desired.Remove(id); stable.Remove(id); Verify(allocator.Process([Up(id)])); }
                else Verify(allocator.Process([Move(id, tick)]));
            }
            var released = allocator.ReleaseAll();
            foreach (var e in released) Check(e.Action == 3 && physical.Remove(e.Slot), "Cleanup emitted an unmatched UP");
            Check(physical.Count == 0 && allocator.ActiveCount == 0 && allocator.PendingCount == 0, "Cleanup leaked a lease");
        }
        Console.WriteLine("PASS 100,000 randomized allocation samples on 1/2/3/10-contact devices: bounds, balance, unique owners and stable leases");
    }

    private static void TestProfilesAndPreview()
    {
        foreach (var count in new[] { 30, 50, 100 })
        {
            var profile = LargeProfile(count);
            var loaded = GamepadProfileStore.DeserializeProfile(GamepadProfileStore.SerializeProfile(profile))!;
            loaded.Validate();
            Check(loaded.Buttons.Count == count && loaded.Buttons.Last().Slot == 999 + count && loaded.Move.Slot == 65000,
                "Large layout lost logical IDs on serialization");
        }
        var high = LargeProfile(1); high.Buttons[0].Slot = GamepadProfile.MaxLogicalSlot; high.Validate();
        var old = GamepadProfileStore.CreateDefaultProfile();
        Check(GamepadProfileStore.SerializeProfile(GamepadProfileStore.DeserializeProfile(GamepadProfileStore.SerializeProfile(old))!) ==
            GamepadProfileStore.SerializeProfile(old), "Existing layout changed during round trip");
        foreach (var rotation in new[] { 0, 1, 2, 3 })
        {
            var profile = LargeProfile(100);
            var device = new TouchDeviceInfo("slots-preview", "", "", 1280, 2772, 2, true, true)
                .WithDisplay(rotation, rotation % 2 == 0 ? 1280 : 2772, rotation % 2 == 0 ? 2772 : 1280);
            var preview = new LocalInputPreview(device, profile, new Random(1));
            var pressed = profile.Buttons.Take(5).Select(b => b.Id).ToHashSet();
            preview.Step(0, 0, 0, false, 0, 0, pressedButtons: pressed);
            Check(preview.Trace.Drain().Count(e => e.Action == 1) == 3 && preview.Slots.PendingCount == 2,
                "Offline preview ignored real contact capacity");
            pressed.Remove(profile.Buttons[3].Id); pressed.Remove(profile.Buttons[0].Id);
            preview.Step(5, 0, 0, false, 0, 0, pressedButtons: pressed);
            var events = preview.Trace.Drain();
            var next = events.Single(e => e.Action == 1);
            var button = profile.Buttons[4];
            Check(Math.Abs(next.X - button.X * (device.DisplayWidth - 1)) < device.DisplayWidth * .02 &&
                Math.Abs(next.Y - button.Y * (device.DisplayHeight - 1)) < device.DisplayWidth * .02,
                "Preview promoted the cancelled control or wrong rotated coordinate");
            preview.ReleaseAll();
            Check(preview.Trace.Drain().Count(e => e.Action == 3) == 3 && preview.Slots.PendingCount == 0,
                "Closing preview leaked leased/waiting contacts");
        }
        Console.WriteLine("PASS 30/50/100-control serialization, high IDs, unchanged legacy layouts and four-rotation limited-capacity canvas preview");
    }

    private static void TestLookDisplacement()
    {
        foreach (var capacity in new[] { 1, 2, 10 })
        foreach (var rotation in new[] { 0, 1, 2, 3 })
        {
            var profile = LargeProfile(100);
            var device = new TouchDeviceInfo("look-leases", "", "", 1280, 2772, capacity - 1, true, true)
                .WithDisplay(rotation, rotation % 2 == 0 ? 1280 : 2772, rotation % 2 == 0 ? 2772 : 1280);
            var preview = new LocalInputPreview(device, profile, new Random(117));
            var fingers = new Dictionary<ushort, (double X, double Y)>();
            double sumX = 0, sumY = 0;
            for (var tick = 0; tick < 2000; tick++)
            {
                preview.Step(tick * 4d, 0, 0, false, tick < 1000 ? 4 : 0, 0);
                foreach (var e in preview.Trace.Drain())
                {
                    Check(e.Slot < capacity, "Look escaped physical capacity");
                    if (e.Action == 1) Check(fingers.TryAdd(e.Slot, (e.X, e.Y)), "Look reused a held lease");
                    if (e.Action == 2)
                    {
                        var old = fingers[e.Slot]; sumX += e.X - old.X; sumY += e.Y - old.Y;
                        fingers[e.Slot] = (e.X, e.Y);
                    }
                    if (e.Action == 3) Check(fingers.Remove(e.Slot), "Look emitted unmatched UP");
                }
                Check(preview.Slots.PendingCount == 0, "Look queued its second finger on single-contact hardware");
            }
            Check(Math.Abs(sumX - 4000 * profile.Look.Sensitivity) <= 1 && Math.Abs(sumY) <= 1,
                $"Physical look displacement was lost: capacity={capacity}, rotation={rotation}, delta={sumX},{sumY}");
            preview.ReleaseAll();
        }
        Console.WriteLine("PASS physical look displacement conserved through dynamic handoff on 1/2/10-contact hardware, all rotations (24,000 samples)");
    }

    private static async Task TestEngine()
    {
        await using var peer = new ReconfigurationTests.Peer();
        using var control = new SkpdbClient(); await control.ConnectAsync(peer.Options);
        await using var engine = new GamepadEngine();
        engine.ConfigureFastPath("127.0.0.1", 0, "", 2, 16);
        var device = new TouchDeviceInfo("slots-engine", "", "", 1280, 2772, 9, true, true);
        var profile = LargeProfile(100);
        await engine.StartAsync(control, device, profile);
        for (var i = 0; i < 100; i++)
        {
            var previous = peer.Events.Count(e => e.Action == 1);
            var button = profile.Buttons[i];
            engine.SetControlPressed(button.Id, true);
            await Until(() => peer.Events.Count(e => e.Action == 1) == previous + 1);
            var down = peer.Events.Last(e => e.Action == 1);
            Check(down.Slot <= device.SlotMax && Math.Abs(down.X - button.X * (device.DisplayWidth - 1)) < 26,
                "Large layout control used an invalid slot/position");
            engine.SetControlPressed(button.Id, false);
            await Until(() => peer.ActiveCount == 0);
        }
        foreach (var button in profile.Buttons.Take(12)) engine.SetControlPressed(button.Id, true);
        await Until(() => peer.ActiveCount == 10 && engine.Metrics.PendingSlots == 2);
        Check(engine.Metrics.SlotCapacity == 10, "Metrics report logical IDs as physical capacity");
        var fullDowns = peer.Events.Count(e => e.Action == 1);
        engine.SetControlPressed(profile.Buttons[10].Id, false);
        await Until(() => engine.Metrics.PendingSlots == 1);
        Check(peer.Events.Count(e => e.Action == 1) == fullDowns, "Full capacity evicted an existing finger");
        engine.SetControlPressed(profile.Buttons[0].Id, false);
        await Until(() => engine.Metrics.PendingSlots == 0 && peer.Events.Count(e => e.Action == 1) == fullDowns + 1);
        engine.SetInputEnabled(false);
        await Until(() => peer.ActiveCount == 0 && engine.Metrics.ActiveSlots == 0);
        engine.SetInputEnabled(true);
        foreach (var button in profile.Buttons.Take(12)) engine.SetControlPressed(button.Id, false);
        // Both look IDs and the pointer ID are above the physical range.
        var relay = (LookTouchRelay)typeof(GamepadEngine).GetField("_lookRelay",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(engine)!;
        engine.SetViewMode(true);
        await Until(() => { engine.AddMouseDelta(4, .1); return relay.ActiveCount == 2 && peer.ActiveCount == 2; });
        engine.SetViewMode(false);
        var beforeClick = peer.Events.Count(e => e.Action == 1);
        engine.PointerTouchDown(.5, .5); engine.PointerTouchUp();
        await Until(() => peer.Events.Count(e => e.Action == 1) > beforeClick && peer.ActiveCount == 0);
        foreach (var button in profile.Buttons.Take(12)) engine.SetControlPressed(button.Id, true);
        await Until(() => engine.Metrics.PendingSlots == 2);
        await engine.ReconfigureAsync(device with { SlotMax = 1 }, profile);
        Check(peer.ActiveCount == 0 && engine.Metrics.PendingSlots == 0 && engine.Metrics.SlotCapacity == 2,
            "Reconfiguration retained waiting touches or stale capacity");
        foreach (var button in profile.Buttons.Take(3)) { engine.SetControlPressed(button.Id, false); engine.SetControlPressed(button.Id, true); }
        await Until(() => engine.Metrics.ActiveSlots == 2 && engine.Metrics.PendingSlots == 1);
        await engine.StopAsync();
        Check(peer.ActiveCount == 0 && engine.Metrics.PendingSlots == 0, "Stop retained touches");
        var active = new HashSet<ushort>();
        foreach (var e in peer.Events)
        {
            Check(e.Slot <= 9, "Logical ID escaped onto the network");
            if (e.Action == 1) Check(active.Add(e.Slot), "Network reused a held physical slot");
            if (e.Action == 2) Check(active.Contains(e.Slot), "Network moved an unassigned slot");
            if (e.Action == 3) active.Remove(e.Slot); // Emergency cleanup can retry UP.
        }
        Check(active.Count == 0 && await control.PingAsync() == "PONG", "Cleanup disrupted control session");
        Console.WriteLine("PASS real client/engine protocol: all 100 controls, 10 held + 2 waiting, cancel/refill, focus release, double-look/mouse transition, smaller-device reconfigure and stop");
    }
}
