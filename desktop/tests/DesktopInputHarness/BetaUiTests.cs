using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkpdbDesktop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

internal static class BetaUiTests
{
    internal static async Task SaveWindowEvidenceAsync(string prefix)
    {
        var image = new RenderTargetBitmap();
        await image.RenderAsync(Field<Grid>(App.MainWindow!, "RootGrid"));
        var pixels = await image.GetPixelsAsync();
        var path = Path.GetFullPath(Path.Combine(".scratch", prefix + "-" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png"));
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        using var pixelReader = DataReader.FromBuffer(pixels);
        var bytes = new byte[pixels.Length]; pixelReader.ReadBytes(bytes);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync(); stream.Seek(0);
        using var encodedReader = new DataReader(stream); await encodedReader.LoadAsync((uint)stream.Size);
        var encoded = new byte[(int)stream.Size]; encodedReader.ReadBytes(encoded); File.WriteAllBytes(path, encoded);
        Console.WriteLine("UI_EVIDENCE " + path);
    }
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static object? Call(object owner, string name, params object[] args) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> value)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!value()) await Task.Delay(30, deadline.Token);
    }
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    public static async Task RunAsync(GamepadPage page)
    {
        var window = App.MainWindow!;
        var nav = Field<NavigationView>(window, "Nav");
        var canvas = Field<Canvas>(page, "LayoutCanvas");
        Check(Field<TextBlock>(window, "BetaBadge").Text == "测试版", "Missing title beta badge");
        Check(nav.IsPaneToggleButtonVisible && nav.PaneDisplayMode == NavigationViewPaneDisplayMode.Left, "Sidebar toggle missing");
        var toggle = Descendants(nav).OfType<Button>().FirstOrDefault(b => b.Name == "TogglePaneButton");
        Check(toggle is not null, "Navigation toggle template button missing");
        var peer = new ButtonAutomationPeer(toggle!);
        var invoke = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke);
        nav.IsPaneOpen = true; await Task.Delay(300);
        var openWidth = canvas.ActualWidth;
        invoke.Invoke(); await Until(() => !nav.IsPaneOpen && canvas.ActualWidth > openWidth + 100);
        var collapsedWidth = canvas.ActualWidth;
        invoke.Invoke(); await Until(() => nav.IsPaneOpen && canvas.ActualWidth < collapsedWidth - 100);
        var bounds = Field<Grid>(page, "PreviewWatermarkBounds");
        var watermark = Field<TextBlock>(page, "PreviewWatermark");
        var rectangle = Field<Windows.Foundation.Rect>(page, "_screenRect");
        Check(!Field<Canvas>(page, "PreviewWatermarkLayer").IsHitTestVisible && !watermark.IsHitTestVisible &&
            watermark.ActualWidth > 50 && watermark.ActualHeight > 10 &&
            watermark.RenderTransform is RotateTransform { Angle: -24 } && bounds.Clip is RectangleGeometry clip &&
            Math.Abs(clip.Rect.Width - rectangle.Width) < 1 && Math.Abs(bounds.Width - rectangle.Width) < 1 &&
            Math.Abs(Canvas.GetLeft(bounds) - rectangle.X) < 1, "Watermark rotation, clipping or hit testing is wrong");
        Console.WriteLine($"PASS title beta badge, slanted clipped watermark and real sidebar toggle: canvas {openWidth:F0} -> {collapsedWidth:F0}");

        var snapshotToggle = Field<ToggleMenuFlyoutItem>(page, "SnapshotVisibleToggle");
        snapshotToggle.IsChecked = false;
        Call(page, "SnapshotVisible_Changed", snapshotToggle, new RoutedEventArgs());
        await Task.Delay(120);
        var image = new RenderTargetBitmap();
        await image.RenderAsync(Field<Grid>(window, "RootGrid"));
        var pixels = await image.GetPixelsAsync();
        var screenshot = Path.GetFullPath(Path.Combine(".scratch", "beta-ui-" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png"));
        using (var stream = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var reader = DataReader.FromBuffer(pixels);
            var bytes = new byte[pixels.Length]; reader.ReadBytes(bytes); reader.Dispose();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, bytes);
            await encoder.FlushAsync();
            stream.Seek(0);
            using var encodedReader = new DataReader(stream);
            await encodedReader.LoadAsync((uint)stream.Size);
            var encoded = new byte[(int)stream.Size]; encodedReader.ReadBytes(encoded);
            File.WriteAllBytes(screenshot, encoded);
        }
        Console.WriteLine("BETA_UI_SCREENSHOT " + screenshot);
        snapshotToggle.IsChecked = true;
        Call(page, "SnapshotVisible_Changed", snapshotToggle, new RoutedEventArgs());

        var session = AppSession.Current;
        var collection = Field<GamepadProfileCollection>(page, "_collection");
        collection.CursorEnabled = false;
        await (Task)Call(page, "StartEngineAsync")!;
        var engine = Field<GamepadEngine>(page, "_engine");
        Check(engine.IsRunning, "HUD start failed: " + Field<TextBlock>(page, "StatusText").Text);
        async Task<JsonNode> State() => JsonNode.Parse((await session.Client.ExecAsync("fixture state")).Output)!;
        Check((await State())["hudEnabled"]!.GetValue<int>() == 1, "HUD missing with cursor disabled");
        var updates = (await State())["hudUpdates"]!.GetValue<int>();
        engine.SetViewMode(true); await (Task)Call(page, "DisableCursorAsync")!;
        await Task.Delay(650);
        var state = await State();
        Check(state["hudEnabled"]!.GetValue<int>() == 1 && state["hudUpdates"]!.GetValue<int>() > updates,
            "View mode or hidden cursor stopped HUD");
        await (Task)Call(page, "StopEngineAsync")!;
        Check((await State())["hudEnabled"]!.GetValue<int>() == 0, "Stop retained HUD");
        await session.Client.ExecAsync("fixture hud-unsupported");
        await (Task)Call(page, "StartEngineAsync")!;
        Check(!engine.IsRunning, "Control started without confirmed HUD support");
        await session.Client.ExecAsync("fixture hud-ok");
        await (Task)Call(page, "StartEngineAsync")!;
        Check(engine.IsRunning, "Control did not recover after compatible module");
        await session.Client.ExecAsync("fixture hud-unsupported");
        await Until(() => !engine.IsRunning);
        await session.Client.ExecAsync("fixture hud-ok");
        await (Task)Call(page, "StopEngineAsync")!;
        Console.WriteLine("PASS real page HUD lifecycle: cursor off, look mode, stop, unsupported module, recovery and heartbeat failure stop");
    }
}
