using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using SkpdbDesktop;

// Exercise the production page and its real foreground watcher, rather than
// duplicating the page's profile-switch policy in a standalone engine test.
internal static class AppSwitchTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static Task Invoke(object owner, string method, params object[] args) =>
        (Task)owner.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, args)!;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate, int seconds = 10)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!predicate()) await Task.Delay(25, deadline.Token);
    }

    public static async Task RunAsync()
    {
        var run = "app_switch_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var folder = Path.GetFullPath(".scratch/mouse-lab/" + run);
        Directory.CreateDirectory(folder);
        var rows = new List<object>();
        var errors = new List<string>();
        var watch = Stopwatch.StartNew();
        var session = AppSession.Current;
        var window = App.MainWindow!;
        GamepadEngine? engine = null;
        GamepadPage? page = null;
        try
        {
            using var operations = await session.Client.OpenIndependentChannelAsync();
            using var http = new HttpClient { BaseAddress = new("http://127.0.0.1:8766"), Timeout = TimeSpan.FromSeconds(4) };
            var host = session.Options!.Host;
            var local = Dns.GetHostEntry(Dns.GetHostName()).AddressList.First(a =>
                a.AddressFamily == AddressFamily.InterNetwork && a.ToString().StartsWith(string.Join('.', host.Split('.').Take(3)) + "."));
            var url = $"http://{local}:8766/";
            await operations.ExecAsync($"input keyevent 224; am start -a android.intent.action.VIEW -d {url}");
            await Task.Delay(2000);
            (await http.PostAsync("/reset", new StringContent(JsonSerializer.Serialize(new { run }),
                System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
            await Until(() => App.MainWindow is not null);
            window.Navigate("gamepad");
            var frame = Field<Frame>(window, "ContentFrame");
            await Until(() => frame.Content is GamepadPage);
            page = (GamepadPage)frame.Content;
            await Until(() => Field<TouchDeviceInfo?>(page, "_device") is not null && Field<ForegroundWatcher?>(page, "_watcher")?.Package is not null);
            var foreground = Field<ForegroundWatcher>(page, "_watcher");
            var browser = foreground.Package!;
            Check(browser != "com.miui.home" && browser != "com.android.settings", "Test page is not foreground");
            engine = Field<GamepadEngine>(page, "_engine");
            await Invoke(page, "StartEngineAsync");
            Check(engine.IsRunning, "Production page failed to start");
            await Task.Delay(300);
            var initial = await operations.GetInfoAsync();
            // Include a drag that is held across the first foreground change.
            // All later injected gestures run exclusively on the safe test page.
            engine.PointerTouchDown(.45, .5);
            await Task.Delay(70);
            engine.PointerTouchMove(.55, .52);
            await Task.Delay(50);
            for (var i = 0; i < 24; i++)
            {
                var command = (i % 3) switch
                {
                    0 => "am start -a android.settings.SETTINGS",
                    1 => "input keyevent 3",
                    _ => $"am start -a android.intent.action.VIEW -d {url}"
                };
                var expected = (i % 3) switch { 0 => "com.android.settings", 1 => "com.miui.home", _ => browser };
                var mode = i % 2 == 1;
                engine.SetViewMode(mode);
                var elapsed = Stopwatch.StartNew();
                await operations.ExecAsync(command);
                // Check running continuously while Android transitions and the
                // 750 ms watcher waits for a stable foreground observation.
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    while (foreground.Package != expected || engine.ActivePackage != expected || Field<bool>(page, "_applyingEnvironment"))
                    {
                        Check(engine.IsRunning && session.IsConnected, "Engine/session stopped during app switch " + i);
                        await Task.Delay(20, timeout.Token);
                    }
                }
                Check(engine.ViewMode == mode, "App switch lost mouse/view mode");
                var info = await operations.GetInfoAsync();
                Check(info.Pid == initial.Pid && info.SessionsTotal == initial.SessionsTotal,
                    "App switch restarted module or opened another TCP session");
                Check(engine.Metrics.ActiveSlots == 0, "App switch retained an old touch");
                rows.Add(new { i, expected, elapsedMs = elapsed.Elapsed.TotalMilliseconds, info.Pid, info.SessionsTotal,
                    running = engine.IsRunning, mode = engine.ViewMode, metrics = engine.Metrics });
                Console.WriteLine($"SWITCH {i + 1}/24 {expected} running={engine.IsRunning} sessionsTotal={info.SessionsTotal}");
                if (i % 3 == 2)
                {
                    engine.SetViewMode(false);
                    engine.PointerTouchDown(.4, .45);
                    await Task.Delay(70);
                    engine.PointerTouchMove(.6, .55);
                    await Task.Delay(100);
                    engine.PointerTouchUp(.6, .55);
                    await Task.Delay(100);
                }
            }
            // Queue several UI requests during one release ACK; keep the last
            // foreground request, even when display updates arrive alongside it.
            var burst = new[] { "test.burst.first", "test.burst.second", browser }
                .Select(p => Invoke(page, "HandleForegroundAsync", p)).ToArray();
            await Task.WhenAll(burst);
            await Until(() => !Field<bool>(page, "_applyingEnvironment"));
            Check(engine.ActivePackage == browser && engine.IsRunning, "Rapid foreground requests lost the latest profile");
            rows.Add(new { burstLatest = engine.ActivePackage, passed = true });
            Field<GamepadProfileCollection>(page, "_collection").Profiles.RemoveAll(p => p.Package.StartsWith("test.burst."));

            await Invoke(page, "StopEngineAsync");
            var device = Field<TouchDeviceInfo>(page, "_device");
            var raw = (await operations.ExecAsync($"/data/local/tmp/skpdb_evdev_state {device.EventPath}")).Output;
            await File.WriteAllTextAsync(Path.Combine(folder, "evdev_final.txt"), raw);
            var tracking = raw.Split('\n').FirstOrDefault(l => l.StartsWith("axis=39 "));
            Check(raw.Contains("btn_touch=0") && tracking is not null &&
                tracking.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).All(v => v == "-1"),
                "Final evdev state has active touches: " + raw);
            // Leaving/re-entering the real page also cancels the watcher. The
            // authenticated main session must remain usable afterwards.
            for (var i = 0; i < 3; i++)
            {
                window.Navigate("home");
                await Task.Delay(1100);
                Check(await session.Client.PingAsync() == "PONG", "Page exit closed main TCP");
                window.Navigate("gamepad");
                await Until(() => frame.Content is GamepadPage p && Field<ForegroundWatcher?>(p, "_watcher")?.Package is not null);
            }
            page = (GamepadPage)frame.Content;
            engine = Field<GamepadEngine>(page, "_engine");
            var result = await http.GetFromJsonAsync<JsonElement>("/results");
            await File.WriteAllTextAsync(Path.Combine(folder, "phone.json"), result.GetRawText());
            Check(result.GetProperty("state").GetProperty("n").GetInt32() == 0, "Phone webpage has stuck touches");
            Console.WriteLine("PASS 24 actual app switches, latest-request burst, 3 page exits; no session churn, engine stop or residual touch");
        }
        catch (Exception error)
        {
            errors.Add(error.ToString());
            Console.WriteLine("APP_SWITCH_FAIL " + error);
        }
        finally
        {
            if (page is not null && engine is { IsRunning: true }) await Invoke(page, "StopEngineAsync");
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new {
                elapsedSeconds = watch.Elapsed.TotalSeconds, rows, errors }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("APP_SWITCH_RESULT " + folder);
        }
    }
}
