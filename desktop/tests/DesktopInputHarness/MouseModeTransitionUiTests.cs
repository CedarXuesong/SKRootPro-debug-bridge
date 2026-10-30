using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using SkpdbDesktop;

internal static class MouseModeTransitionUiTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object[] args) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
    private static async Task<JsonNode> State() =>
        JsonNode.Parse((await AppSession.Current.Client.ExecAsync("fixture state")).Output)!;
    private static int Downs(JsonNode state) => state["downs"]!.AsObject().Sum(p => p.Value!.GetValue<int>());
    private static async Task WaitForReleasedClicks(int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var state = await State();
            if (Downs(state) >= expected && state["active"]!.AsArray().Count == 0) return;
            await Task.Delay(10, timeout.Token);
        }
    }

    public static async Task RunAsync(GamepadPage page)
    {
        var engine = Field<GamepadEngine>(page, "_engine");
        var capture = Field<MouseCapture>(page, "_mouseCapture");
        var timer = Field<DispatcherQueueTimer>(page, "_statsTimer");
        var device = Field<TouchDeviceInfo>(page, "_device");
        var profile = Field<GamepadProfile>(page, "_profile");
        var full = profile.Clone();
        var lastDisplayCheck = Field<DateTime>(page, "_lastDisplayCheck");
        full.Buttons = Enumerable.Range(2, 8).Select(slot => new GamepadButton
        {
            Id = "full" + slot, Slot = slot,
            Binding = slot switch { 2 => "MouseRight", 4 => "Tab", 5 => "MouseLeft", _ => "F" + slot },
            Kind = slot == 4 ? GamepadControlKind.Look : GamepadControlKind.Fire
        }).ToList();
        timer.Stop(); // Drive geometry explicitly, without automatic display polling.
        Set(page, "_lastDisplayCheck", DateTime.UtcNow.AddMinutes(5));
        try
        {
            Set(page, "_profile", full);
            await (Task)Call(page, "StartEngineAsync")!;
            Check(engine.IsRunning, "Full-layout UI engine did not start");
            foreach (var rotation in new[] { 1, 3, 0, 2 })
            {
                var landscape = rotation is 1 or 3;
                var rotated = device.WithDisplay(rotation,
                    landscape ? Math.Max(device.ScreenWidth, device.ScreenHeight) : Math.Min(device.ScreenWidth, device.ScreenHeight),
                    landscape ? Math.Min(device.ScreenWidth, device.ScreenHeight) : Math.Max(device.ScreenWidth, device.ScreenHeight));
                Set(page, "_device", rotated);
                await engine.ReconfigureAsync(rotated, full);
                Call(page, "SyncPlayModeUi", true);
                Check(engine.DirectTouchAvailable, "Full-layout UI has pointer=none");
                var before = Downs(await State());
                // Run this whole pair on the UI thread, without yielding to
                // Engine.Changed: capture is still hidden when mouse mode starts.
                for (var i = 0; i < 6; i++)
                {
                    engine.SetViewMode(true);
                    Call(page, "SyncPlayModeUi", true);
                    Check(capture.IsHidden, "Expected hidden view capture");
                    var state = Field<object>(capture, "_state");
                    var region = (PointerCaptureRegion)state.GetType().GetProperty("Region")!.GetValue(state)!;
                    var x = region.Left + (int)((region.Right - region.Left) * .37);
                    var y = region.Top + (int)((region.Bottom - region.Top) * .64);
                    engine.SetViewMode(false);
                    if (i % 2 == 1) capture.Release(); // Interrupted capture must recover on the next click.
                    Call(page, "OnNativeMouseButton", "MouseLeft", true, x, y);
                    Check(!capture.IsHidden && capture.IsActive && Field<bool>(page, "_directTouchActive"),
                        "First mouse-mode click was lost before queued UI synchronization");
                    Call(page, "OnNativeMouseButton", "MouseLeft", false, x, y);
                    await WaitForReleasedClicks(before + i + 1);
                }
                var after = await State();
                Check(Downs(after) == before + 6, "Full-layout mouse mode lost or duplicated repeated clicks");
                Check(after["active"]!.AsArray().Count == 0, "Mode transition retained remote touches");
            }
            Console.WriteLine("PASS full-layout WinUI: 24 immediate view-to-mouse clicks across four rotations, stale hidden capture and lost-capture recovery");
        }
        finally
        {
            await (Task)Call(page, "StopEngineAsync")!;
            Set(page, "_device", device);
            Set(page, "_profile", profile);
            Set(page, "_lastDisplayCheck", lastDisplayCheck);
            Call(page, "DrawLayout");
            timer.Start();
        }
    }
}
