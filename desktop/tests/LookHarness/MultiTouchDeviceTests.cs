using SkpdbDesktop;
using System.Text;
using System.Text.Json;

internal static class MultiTouchDeviceTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, deadline.Token);
    }

    public static async Task RunAsync(string host, string key)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var client = new SkpdbClient(); await client.ConnectAsync(new(host, Key: key));
        var module = await client.GetInfoAsync();
        Check(module.GestureLeaseSupported, "Update the module for automatic system gesture coordination");
        var originalGesture = (await client.ExecAsync("settings get system enable_three_gesture")).Output.Trim();
        await using var gesture = await GestureLeaseSession.StartAsync(client);
        var touch = await client.TouchInfoAsync();
        Check(touch.Contains("virtual_device=0"), "Backend is not the physical touchscreen");
        var device = TouchDeviceInfo.Parse(touch);
        Check(device.SlotMax == 9, "This device regression requires a 10-contact panel");
        var display = TouchDeviceInfo.ParseDisplay((await client.ExecAsync("dumpsys display")).Output);
        if (display is { } d) device = device.WithDisplay(d.Rotation, d.Width, d.Height);
        var report = new List<int>();
        async Task<int[]> Slots()
        {
            var state = await client.ExecAsync("/data/local/tmp/skpdb-touch-event-probe --state " + device.EventPath);
            Check(state.ExitCode == 0, "Could not read actual touchscreen slots");
            return state.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        }
        async Task<int[]> Count(int expected)
        {
            var slots = await Slots(); var active = slots.Count(id => id >= 0);
            report.Add(active);
            Check(active == expected, $"Expected {expected} physical contacts, got {active}");
            Check(slots.Where(id => id >= 0).Distinct().Count() == active, "Tracking IDs collided");
            return slots;
        }
        var profile = DynamicSlotTests.LargeProfile(12);
        profile.Move.Enabled = profile.Look.Enabled = false;
        for (var i = 0; i < profile.Buttons.Count; i++)
        {
            var button = profile.Buttons[i];
            button.Binding = "F" + (i + 1);
            button.Kind = i % 2 == 0 ? GamepadControlKind.Tap : GamepadControlKind.Skill;
            button.X = .2 + i % 5 * .14; button.Y = .3 + i / 5 * .15;
        }
        await using var engine = new GamepadEngine();
        engine.ConfigureFastPath(host, module.FastPort, module.FastToken, module.FastProtocol, module.FastMaxRecords);
        try
        {
            var started = await client.ExecAsync("input keyevent 224; am start -W -n com.skpdb.touchtest/.MainActivity");
            Check(started.ExitCode == 0, "Could not open the inert touch receiver");
            using (var focusDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (!(await client.ExecAsync("dumpsys window | grep mCurrentFocus", focusDeadline.Token)).Output.Contains("com.skpdb.touchtest"))
                    await Task.Delay(50, focusDeadline.Token);
            var devices = (await client.ExecAsync("cat /proc/bus/input/devices")).Output;
            Check(originalGesture != "1" || (await client.ExecAsync("settings get system enable_three_gesture")).Output.Trim() == "0",
                "Conflicting three-finger gesture was not suspended");
            await Count(0);
            await engine.StartAsync(client, device, profile);
            int[]? previous = null;
            for (var i = 0; i < 12; i++)
            {
                engine.SetKey("F" + (i + 1), true);
                await Until(() => engine.Metrics.ActiveSlots == Math.Min(i + 1, 10) &&
                    engine.Metrics.PendingSlots == Math.Max(i - 9, 0));
                await Task.Delay(100);
                var current = await Count(Math.Min(i + 1, 10));
                if (previous is not null)
                    Check(previous.Where(id => id >= 0).All(current.Contains), "Adding a key lifted an existing finger");
                previous = current;
            }
            await Task.Delay(300); var full = await Count(10);
            engine.SetKey("F11", false); await Until(() => engine.Metrics.PendingSlots == 1);
            engine.SetKey("F1", false); await Until(() => engine.Metrics.PendingSlots == 0);
            await Task.Delay(100); var refilled = await Count(10);
            Check(full.Intersect(refilled).Count(id => id >= 0) == 9, "FIFO refill changed other held tracking IDs");
            foreach (var i in Enumerable.Range(1, 12)) engine.SetKey("F" + i, false);
            await Until(() => engine.Metrics.ActiveSlots == 0); await Count(0);
            foreach (var i in Enumerable.Range(1, 10)) engine.SetKey("F" + i, true);
            await Until(() => engine.Metrics.ActiveSlots == 10); await Task.Delay(100); await Count(10);
            await engine.StopAsync(); await Count(0);
            Check(await client.PingAsync() == "PONG", "Multi-key test lost its main connection");
            Check((await client.ExecAsync("cat /proc/bus/input/devices")).Output == devices, "Input device inventory changed");
            var events = (await client.ExecAsync("cat /data/user/0/com.skpdb.touchtest/files/events.tsv")).Output;
            var maximum = events.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t'))
                .Where(parts => parts.Length >= 4).Select(parts => int.Parse(parts[2])).DefaultIfEmpty().Max();
            Check(maximum == 10, $"Android receiver only observed {maximum} pointers");
            await gesture.DisposeAsync();
            Check((await client.ExecAsync("settings get system enable_three_gesture")).Output.Trim() == originalGesture,
                "System gesture setting was not restored");
            Console.WriteLine("TOUCH_CHORD_DEVICE_PASS " + JsonSerializer.Serialize(new
            { counts = report, androidMaxPointers = maximum, newInputDevice = false, mainPing = true, cleanedUp = true,
                gestureRestored = true, dfmLaunched = false }));
        }
        finally
        {
            await engine.StopAsync();
            await gesture.DisposeAsync();
            await client.ExecAsync("am force-stop com.skpdb.touchtest");
        }
    }
}
