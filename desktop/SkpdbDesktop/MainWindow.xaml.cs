using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;

namespace SkpdbDesktop;

public sealed partial class MainWindow : Window
{
    public bool OfflinePreviewRequested { get; set; }
    public static MainWindow? Instance { get; private set; }
    private GamepadPage? _gamepadPage;
    private Task? _closingTask;
    private bool _closeApproved;

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public bool IsForeground
    {
        get
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            return GetForegroundWindow() == hwnd;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public double ViewportWidthDip => ViewportDip(width: true);

    public double ViewportHeightDip => ViewportDip(width: false);

    private double ViewportDip(bool width)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        if (!GetClientRect(hwnd, out var rect))
        {
            return 0;
        }
        var scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1;
        }
        var pixels = width ? rect.Right - rect.Left : rect.Bottom - rect.Top;
        return Math.Max(0, pixels / scale);
    }

    public MainWindow()
    {
        InitializeComponent();
        CanaryBadge.Visibility = BuildInfo.IsCanary ? Visibility.Visible : Visibility.Collapsed;
        ContentFrame.Navigated += (_, e) => { if (e.Content is GamepadPage page) _gamepadPage = page; };
        AppWindow.Closing += (closingWindow, args) =>
        {
            if (_closeApproved) return;
            args.Cancel = true;
            _ = CloseAfterCleanupAsync();
        };
        Instance = this;
        Title = BuildInfo.IsCanary ? "SKPDB Canary" : "SKPDB";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        AppWindow.Resize(new SizeInt32(1280, 840));
        CenterOnScreen();
        ApplyTheme(SettingsStore.Load().DarkTheme);
        Nav.SelectedItem = Nav.MenuItems[0];
        ContentFrame.Navigate(typeof(HomePage));
        Closed += (_, _) => AppSession.Current.Client.Dispose();
    }

    public Task CloseAfterCleanupAsync() => _closingTask ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        try
        {
            if (_gamepadPage is not null) await _gamepadPage.ShutdownAsync();
        }
        catch (Exception error) { BufferedLog.Write("Window cleanup: " + error); }
        finally
        {
            _closeApproved = true;
            AppSession.Current.Client.Dispose();
            Close();
        }
    }

    public void Navigate(string tag)
    {
        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem navItem &&
                string.Equals(navItem.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                Nav.SelectedItem = navItem;
                break;
            }
        }
    }

    public void ApplyTheme(bool dark)
    {
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor =
                dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveForegroundColor =
                dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 96, 96, 96);
        }
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        AppWindow.Move(new PointInt32(
            work.X + (work.Width - 1280) / 2,
            work.Y + (work.Height - 840) / 2));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var tag = item.Tag as string;
        var page = tag switch
        {
            "terminal" => typeof(TerminalPage),
            "files" => typeof(FilesPage),
            "memory" => typeof(MemoryPage),
            "touch" => typeof(TouchPage),
            "gamepad" => typeof(GamepadPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(HomePage)
        };

        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }
}
