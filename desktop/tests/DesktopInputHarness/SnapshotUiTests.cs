using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using SkpdbDesktop;

internal static class SnapshotUiTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static object? Call(object owner, string name, params object[] args) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(30, deadline.Token);
    }
    private static async Task CaptureAsync(GamepadPage page)
    {
        Call(page, "CaptureSnapshot_Click", page, new RoutedEventArgs());
        await Until(() => Field<CancellationTokenSource?>(page, "_snapshotCapture") is null);
    }

    public static async Task RunAsync()
    {
        try
        {
            var session = AppSession.Current;
            await session.Client.ExecAsync("fixture ok");
            var window = App.MainWindow!;
            // AppWindow sizes use physical pixels; keep the fixture usable at high DPI.
            var scale = window.AppWindow.Size.Width / Math.Max(1, window.ViewportWidthDip);
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Ceiling(1280 * scale), (int)Math.Ceiling(900 * scale)));
            window.Navigate("gamepad");
            var frame = Field<Frame>(window, "ContentFrame");
            await Until(() => frame.Content is GamepadPage page && Field<bool>(page, "_pageReady") &&
                Field<TouchDeviceInfo?>(page, "_device") is not null &&
                Field<ForegroundWatcher?>(page, "_watcher")?.Package is { } package &&
                Field<GamepadProfile>(page, "_profile").Package == package &&
                !Field<bool>(page, "_applyingEnvironment"));
            var page = (GamepadPage)frame.Content;
            await Until(() => Field<Canvas>(page, "LayoutCanvas").ActualWidth > 20 &&
                Field<Canvas>(page, "LayoutCanvas").ActualHeight > 20);
            await CaptureAsync(page);
            var first = Field<ScreenSnapshot?>(page, "_snapshot");
            Check(first is not null, "Capture failed: " + Field<TextBlock>(page, "StatusText").Text);
            var canvas = Field<Canvas>(page, "LayoutCanvas");
            Check(canvas.Children.OfType<Image>().Any(),
                $"Snapshot background missing: canvas={canvas.ActualWidth}x{canvas.ActualHeight}, " +
                $"device={Field<TouchDeviceInfo>(page, "_device")}, snapshot={first}, " +
                $"status={Field<TextBlock>(page, "SnapshotStatusText").Text}");
            var image = canvas.Children.OfType<Image>().Single();
            Check(image.Source is SoftwareBitmapSource && first!.Width == 1024 && first.Height == 512, "PNG did not decode in WinUI");
            Check(first!.Png.Length == 0, "Compressed PNG retained after decode");
            Check(canvas.Children.IndexOf(image) == 1 && canvas.Children.Count > 2 && !image.IsHitTestVisible,
                "Snapshot must appear below editable layout and ignore pointer hit testing");
            Console.WriteLine("PASS real WinUI PNG decode and canvas background below interactive overlays");
            await CanaryUiTests.RunAsync(page);

            var toggle = Field<ToggleMenuFlyoutItem>(page, "SnapshotVisibleToggle");
            toggle.IsChecked = false;
            Call(page, "SnapshotVisible_Changed", toggle, new RoutedEventArgs());
            Check(!canvas.Children.OfType<Image>().Any(), "Hide did not remove canvas background");
            toggle.IsChecked = true;
            Call(page, "SnapshotVisible_Changed", toggle, new RoutedEventArgs());
            Check(canvas.Children.OfType<Image>().Count() == 1, "Show did not restore canvas background");
            await session.Client.ExecAsync("fixture fail");
            await CaptureAsync(page);
            Check(ReferenceEquals(first, Field<ScreenSnapshot>(page, "_snapshot")) && canvas.Children.OfType<Image>().Any(),
                "Failed capture replaced the previous valid frame");
            await session.Client.ExecAsync("fixture ok");
            await session.Client.ExecAsync("fixture invalid");
            await CaptureAsync(page);
            Check(ReferenceEquals(first, Field<ScreenSnapshot>(page, "_snapshot")), "Corrupted PNG replaced valid image");
            Console.WriteLine("PASS hide/show and preservation after shell or PNG checksum failure");

            await session.Client.ExecAsync("fixture ok");
            await session.Client.ExecAsync("fixture rotate");
            await (Task)Call(page, "RefreshDisplayAsync")!;
            await Until(() => Field<TouchDeviceInfo>(page, "_device").Rotation == 2);
            Call(page, "UpdateSnapshotUi");
            Check(!canvas.Children.OfType<Image>().Any(), "180-degree rotation must hide stale snapshot, even at same dimensions");
            await CaptureAsync(page);
            Check(Field<ScreenSnapshot>(page, "_snapshot").Rotation == 2 && canvas.Children.OfType<Image>().Any(),
                "Recapture did not align the newly rotated background");
            var current = Field<ScreenSnapshot>(page, "_snapshot");
            Check(first.Id != current.Id, "Recapture reused frame identity");
            Call(page, "ClearSnapshot_Click", page, new RoutedEventArgs());
            Check(Field<ScreenSnapshot?>(page, "_snapshot") is null && Field<SoftwareBitmapSource?>(page, "_snapshotImage") is null &&
                !canvas.Children.OfType<Image>().Any(), "Clear retained memory or canvas image");
            Console.WriteLine("PASS rotation alignment, timestamped recapture and clearing memory/image");

            await CaptureAsync(page);
            var heldImage = canvas.Children.OfType<Image>().Single();
            window.Navigate("home");
            await Until(() => frame.Content is not GamepadPage);
            Check(Field<ScreenSnapshot?>(page, "_snapshot") is null && heldImage.Source is null,
                "Cached page retains decoded image after navigation");
            Check(await session.Client.PingAsync() == "PONG", "Page exit closed primary session");
            Check(!Directory.EnumerateFiles(AppPaths.DataDirectory, "*.png", SearchOption.AllDirectories).Any(),
                "Snapshot was written to disk");
            window.Navigate("gamepad");
            await Until(() => frame.Content is GamepadPage p && Field<Button>(p, "CaptureSnapshotButton").IsEnabled);
            await CaptureAsync((GamepadPage)frame.Content);
            Check(Field<ScreenSnapshot?>(frame.Content, "_snapshot") is not null,
                "Revisited page cannot capture: " + Field<TextBlock>(frame.Content, "StatusText").Text);
            Console.WriteLine("PASS page exit releases image, primary stays connected and no PNG is saved locally");
            Console.WriteLine("SNAPSHOT_UI_PASS");
        }
        catch (Exception error)
        {
            Console.WriteLine("SNAPSHOT_UI_FAIL " + error);
        }
    }
}
