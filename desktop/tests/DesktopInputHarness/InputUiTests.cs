using System.Reflection;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkpdbDesktop;
using Windows.Foundation;

internal static class InputUiTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object[] args) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!predicate()) await Task.Delay(20, deadline.Token);
    }
    private static async Task<JsonNode> State() => JsonNode.Parse((await AppSession.Current.Client.ExecAsync("fixture state")).Output)!;
    private static int Downs(JsonNode state, int slot) => state["downs"]?[slot.ToString()]?.GetValue<int>() ?? 0;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    private static void ActivateFixture(MainWindow window)
    {
        window.AppWindow.Show(); window.Activate();
        if (window.IsForeground) return;
        var current = GetCurrentThreadId();
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try { SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)); window.Activate(); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }

    public static async Task RunAsync()
    {
        GamepadPage? page = null;
        try
        {
            var session = AppSession.Current;
            var window = App.MainWindow!;
            ActivateFixture(window); window.Navigate("gamepad");
            var frame = Field<Frame>(window, "ContentFrame");
            await Until(() => frame.Content is GamepadPage p && Field<bool>(p, "_pageReady") &&
                Field<ForegroundWatcher?>(p, "_watcher")?.Package is { } package && Field<GamepadProfile>(p, "_profile").Package == package &&
                !Field<bool>(p, "_applyingEnvironment"));
            page = (GamepadPage)frame.Content;
            // Navigation/initial layout can occur after the first activation.
            // Acquire foreground again only once the actual fixture is ready.
            ActivateFixture(window);
            await Task.Delay(80);
            var profile = GamepadProfileStore.CreateDefaultProfile();
            profile.Buttons = new()
            {
                new() { Id = "left", Slot = 2, Binding = "MouseLeft", Kind = GamepadControlKind.Fire, X = .84, Y = .9 },
                new() { Id = "right", Slot = 3, Binding = "MouseRight", Kind = GamepadControlKind.RightWalk },
                new() { Id = "middle", Slot = 4, Binding = "MouseMiddle", Kind = GamepadControlKind.Crosshair },
                new() { Id = "mode", Slot = 5, Binding = "Tab", Kind = GamepadControlKind.Look },
                new() { Id = "key", Slot = 6, Binding = "F", Kind = GamepadControlKind.Tap }
            };
            profile.Validate(); Set(page, "_profile", profile);
            Field<GamepadProfileCollection>(page, "_collection").CursorEnabled = false;
            await (Task)Call(page, "StartEngineAsync")!;
            var engine = Field<GamepadEngine>(page, "_engine");
            Check(engine.IsRunning, "Engine failed: " + Field<TextBlock>(page, "StatusText").Text);
            var capture = Field<MouseCapture>(page, "_mouseCapture");
            Console.WriteLine($"UI_DIAGNOSTIC foreground={window.IsForeground} canvas={Field<Canvas>(page, "LayoutCanvas").ActualWidth}x{Field<Canvas>(page, "LayoutCanvas").ActualHeight} rect={Field<Rect>(page, "_screenRect")} capture={capture.IsActive}");
            JsonNode state;
            if (!window.IsForeground)
            {
                Console.WriteLine("SKIP native page input: interactive foreground is unavailable in this session; engine input regressions run separately");
                await (Task)Call(page, "StopEngineAsync")!;
            }
            else
            {
                await Until(() => capture.IsActive);
                var captureState = Field<object>(capture, "_state");
                var region = (PointerCaptureRegion)captureState.GetType().GetProperty("Region")!.GetValue(captureState)!;
                var x = region.Left + (int)((region.Right - region.Left) * .84);
                var y = region.Top + (int)((region.Bottom - region.Top) * .9);
                void Mouse(string button, bool down) => Call(page, "OnNativeMouseButton", button, down, x, y);
                foreach (var button in new[] { "MouseRight", "MouseMiddle", "MouseLeft" }) { Mouse(button, true); Mouse(button, false); }
                await Task.Delay(140);
                state = await State();
                Check(Downs(state, Field<ushort>(engine, "_pointerTouchSlot")) == 1 && Downs(state, 2) + Downs(state, 3) + Downs(state, 4) == 0,
                    "Native page mouse-mode route fired a binding or hit an overlay");
                Console.WriteLine("PASS production WinUI native mouse route: overlay left click touches coordinates; right/middle do not fire bindings");

                engine.SetViewMode(true); Call(page, "SyncPlayModeUi", true);
                Mouse("MouseLeft", true); Mouse("MouseRight", true);
                await Task.Delay(80);
                state = await State();
                Check(Downs(state, 2) == 1 && Downs(state, 3) == 1, "View route hit hidden-cursor controls instead of bindings");
                Mouse("MouseLeft", false); Mouse("MouseRight", false);
                Call(page, "OnGlobalKey", 0x46, true); Call(page, "OnGlobalKey", 0x46, true); Call(page, "OnGlobalKey", 0x46, false);
                await Task.Delay(60); state = await State();
                Check(Downs(state, 6) == 1, "Native keyboard repeat duplicated the action");
                Call(page, "OnGlobalKey", 0x1B, true);
                await Until(() => engine.IsMouseMode);
                Call(page, "OnGlobalKey", 0x1B, true); await Task.Delay(50);
                Check(engine.IsRunning, "Repeated held Esc stopped the engine after exiting view");
                Call(page, "OnGlobalKey", 0x1B, false); Call(page, "OnGlobalKey", 0x1B, true); Call(page, "OnGlobalKey", 0x1B, false);
                await Until(() => !engine.IsRunning);
                Check((await State())["active"]!.AsArray().Count == 0, "Stopping through Esc retained touches");
                Console.WriteLine("PASS WinUI view bindings, repeat deduplication and Esc: first press exits view, second fresh press stops and releases");
                await MouseModeTransitionUiTests.RunAsync(page);
            }

            state = await State(); var touchReads = state["touchInfoReads"]!.GetValue<int>();
            var version = Field<int>(page, "_environmentVersion");
            for (var i = 0; i < 30; ++i) session.AddActivity("fixture activity " + i);
            await Task.Delay(120);
            Check(Field<int>(page, "_environmentVersion") == version && (await State())["touchInfoReads"]!.GetValue<int>() == touchReads,
                "Activity logs reinitialized the device");
            var hitProfile = profile.Clone();
            hitProfile.Buttons[1].X = hitProfile.Buttons[0].X; hitProfile.Buttons[1].Y = hitProfile.Buttons[0].Y;
            Set(page, "_profile", hitProfile);
            var hit = ((string?, string?))Call(page, "HitTest", new Point(.84, .9))!;
            Check(hit.Item2 == "right", "Editor overlap picked the covered bottom control");
            var screen = Field<Rect>(page, "_screenRect");
            var outside = ((string?, string?))Call(page, "HitTest", new Point(.84, .9 + .06 * screen.Width / screen.Height + .04))!;
            Check(outside.Item2 != "right", "Editor button hit-test stretched a circle into an ellipse");
            hitProfile.Look.AnchorX = hitProfile.Move.CenterX; hitProfile.Look.AnchorY = hitProfile.Move.CenterY;
            var covered = ((string?, string?))Call(page, "HitTest", new Point(hitProfile.Move.CenterX, hitProfile.Move.CenterY))!;
            Check(covered.Item1 == "look", "Covered move component won over the visible look region");
            Console.WriteLine("PASS activity notifications preserve initialization; editor hits the visible top layer with the rendered circular bounds");

            foreach (var kind in new[] { GamepadControlKind.Fire, GamepadControlKind.RightWalk, GamepadControlKind.Crosshair })
            {
                var button = new GamepadButton { Kind = kind, Mode = GamepadButtonMode.Rapid };
                Call(page, "FillButtonFields", button, GamepadControlCatalog.Info(kind));
                Check(Field<ComboBox>(page, "ButtonModeBox").SelectedItem is ComboBoxItem { Tag: GamepadButtonMode.Rapid } &&
                    Field<StackPanel>(page, "IntervalFields").Visibility == Visibility.Visible,
                    "Editing rapid hold-control lost its mode or hid interval");
            }
            foreach (var kind in new[] { GamepadControlKind.Scroll, GamepadControlKind.Zoom })
            {
                Call(page, "FillButtonFields", new GamepadButton { Kind = kind }, GamepadControlCatalog.Info(kind));
                Check(Field<NumberBox>(page, "DurationBox").Visibility == Visibility.Visible, "Timed pulse duration hidden in editor");
            }
            Console.WriteLine("PASS actual editor retains Rapid modes and exposes rapid intervals plus Scroll/Zoom duration");
            await DynamicSlotUiTests.RunAsync(page);
            await EditorUiTests.RunAsync(page);

            for (var i = 0; i < 8; ++i) { window.Navigate("home"); window.Navigate("gamepad"); }
            await Until(() => frame.Content is GamepadPage p && Field<bool>(p, "_pageReady") &&
                Field<bool>(p, "_inputAttached") && Field<ForegroundWatcher?>(p, "_watcher")?.Package is not null);
            page = (GamepadPage)frame.Content;
            await Task.Delay(150);
            Check(Field<bool>(page, "_inputAttached") && Field<MouseCapture>(page, "_mouseCapture").IsActive == false,
                "A stale Unloaded detached the returned page or retained stopped capture");
            Check(await session.Client.PingAsync() == "PONG", "Page transitions closed primary TCP");
            Console.WriteLine("PASS 8 immediate page leave/return cycles retain one active page lifecycle and preserve primary TCP");

            window.Navigate("files");
            await Until(() => frame.Content is FilesPage);
            var files = (FilesPage)frame.Content;
            await Until(() => Field<int>(files, "_busyCount") == 0);
            Field<TextBox>(files, "PathBox").Text = "/slow";
            var slow = (Task)Call(files, "RefreshAsync")!;
            Field<TextBox>(files, "PathBox").Text = "/fast";
            var fast = (Task)Call(files, "RefreshAsync")!;
            await fast;
            Check(Field<ListView>(files, "FileList").Items.Cast<FileEntry>().Single().Name == "fast.txt", "Newest directory not shown");
            Check(Field<ProgressRing>(files, "Progress").IsActive, "Overlapping refresh hid the remaining progress");
            await slow;
            Check(Field<ListView>(files, "FileList").Items.Cast<FileEntry>().Single().Name == "fast.txt" &&
                !Field<ProgressRing>(files, "Progress").IsActive, "Late old directory overwrote the new one or left progress active");
            Check(await session.Client.PingAsync() == "PONG", "File requests blocked/closed primary TCP");
            Console.WriteLine("PASS real file page ignores late directory replies and tracks concurrent work on independent channels");
            await PageOperationUiTests.RunAsync(window);
            window.Navigate("gamepad");
            await Until(() => frame.Content is GamepadPage p && Field<bool>(p, "_pageReady"));
            page = (GamepadPage)frame.Content;
            await (Task)Call(page, "StartEngineAsync")!;
            engine = Field<GamepadEngine>(page, "_engine");
            Check(engine.IsRunning, "Shutdown fixture failed to start: " + Field<TextBlock>(page, "StatusText").Text);
            engine.SetInputEnabled(true); // API-driven cleanup test, independent of OS foreground availability.
            engine.SetKey("W", true);
            await Until(() => engine.Metrics.ActiveSlots > 0);
            await page.ShutdownAsync();
            Check(!engine.IsRunning && engine.Metrics.ActiveSlots == 0 && !Field<bool>(page, "_inputAttached") &&
                !Field<MouseCapture>(page, "_mouseCapture").IsActive, "Page shutdown retained input resources");
            Check((await State())["active"]!.AsArray().Count == 0 && await session.Client.PingAsync() == "PONG",
                "Shutdown retained remote contacts or closed primary TCP before release");
            Console.WriteLine("PASS production page shutdown releases active engine, hooks, mouse capture and remote contacts before primary disposal");
            Console.WriteLine("INPUT_UI_PASS");
        }
        catch (Exception error) { Console.WriteLine("INPUT_UI_FAIL " + error); }
        finally { if (page is not null) await (Task)Call(page, "StopEngineAsync")!; }
    }
}
