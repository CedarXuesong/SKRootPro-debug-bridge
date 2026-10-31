using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using SkpdbDesktop;

internal static class DeviceOverlayUiTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static Task Invoke(object owner, string name) =>
        (Task)owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null)!;
    public static async Task RunAsync()
    {
        try
        {
            var window = App.MainWindow!;
            window.Navigate("gamepad");
            var frame = Field<Frame>(window, "ContentFrame");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (frame.Content is not GamepadPage p || !Field<bool>(p, "_pageReady") || Field<TouchDeviceInfo?>(p, "_device") is null)
                await Task.Delay(50, timeout.Token);
            var page = (GamepadPage)frame.Content;
            var engine = Field<GamepadEngine>(page, "_engine");
            var settings = Field<GamepadProfileCollection>(page, "_collection");
            for (var i = 0; i < 2; i++)
            {
                settings.CursorEnabled = i == 1;
                await Invoke(page, "StartEngineAsync");
                var expectsOverlay = BuildInfo.UseDebugOverlay(AppSession.Current.Info);
                if (!engine.IsRunning || (Field<ControlOverlaySession?>(page, "_controlOverlay") is not null) != expectsOverlay)
                    throw new InvalidOperationException("Real device start failed: " + Field<TextBlock>(page, "StatusText").Text);
                await Task.Delay(2200, timeout.Token);
                if (!engine.IsRunning || Field<ControlOverlaySession?>(page, "_controlOverlay")?.Failure is not null)
                    throw new InvalidOperationException("Real device heartbeat failed");
                if (i == 1 && !Field<bool>(page, "_cursorActive"))
                    throw new InvalidOperationException("Real cursor channel failed");
                await Invoke(page, "StopEngineAsync");
                if (engine.IsRunning || Field<ControlOverlaySession?>(page, "_controlOverlay") is not null)
                    throw new InvalidOperationException("Real page stop retained HUD session");
                Console.WriteLine($"PASS real USB page start/heartbeat/stop, cursor enabled={i == 1}");
            }
            await page.ShutdownAsync();
            Console.WriteLine("DEVICE_OVERLAY_UI_PASS");
        }
        catch (Exception error) { Console.WriteLine("DEVICE_OVERLAY_UI_FAIL " + error); }
    }
}
