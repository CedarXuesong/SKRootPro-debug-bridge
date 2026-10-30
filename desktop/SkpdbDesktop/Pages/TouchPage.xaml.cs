using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace SkpdbDesktop;

public sealed partial class TouchPage : Page
{
    private readonly AppSession _session = AppSession.Current;
    private bool _busy, _pageActive;
    private int _pageVersion;

    public TouchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    { base.OnNavigatedTo(e); _pageActive = true; }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    { _pageActive = false; ++_pageVersion; base.OnNavigatedFrom(e); }

    private async void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync((client, token) => client.TouchInfoAsync(token), "查询触摸设备");
    }

    private async void TapButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidGesture(false)) return;
        var x = (int)XBox.Value; var y = (int)YBox.Value;
        var duration = (int)DurationBox.Value; var slot = (int)SlotBox.Value;
        await RunAsync((client, token) => client.TouchTapAsync(x, y, duration, slot, token), "发送点击");
    }

    private async void SwipeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidGesture(true)) return;
        var x = (int)XBox.Value; var y = (int)YBox.Value; var x2 = (int)X2Box.Value; var y2 = (int)Y2Box.Value;
        var duration = (int)DurationBox.Value; var slot = (int)SlotBox.Value;
        await RunAsync((client, token) => client.TouchSwipeAsync(x, y, x2, y2, duration, slot, token), "发送滑动");
    }

    private bool ValidGesture(bool swipe)
    {
        static bool InRange(double value, int min, int max) => double.IsFinite(value) && value >= min && value <= max;
        if (InRange(XBox.Value, 0, 20000) && InRange(YBox.Value, 0, 20000) &&
            (!swipe || InRange(X2Box.Value, 0, 20000) && InRange(Y2Box.Value, 0, 20000)) &&
            InRange(DurationBox.Value, 1, 10000) && InRange(SlotBox.Value, 0, 9)) return true;
        OutputText.Text = "请输入有效的坐标、时长和 slot。"; return false;
    }

    private async Task RunAsync(Func<SkpdbClient, CancellationToken, Task<string>> action, string activity)
    {
        if (_busy || !_pageActive) return;
        if (!_session.IsConnected)
        {
            OutputText.Text = "请先在主页连接设备。";
            return;
        }
        var generation = _session.Client.ConnectionGeneration; var version = _pageVersion;
        bool Current() => _pageActive && version == _pageVersion && generation == _session.Client.ConnectionGeneration;
        _busy = true; SetBusy(true);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_session.Options?.TimeoutSeconds ?? 10, 15, 120)));
            using var channel = await _session.Client.OpenIndependentChannelAsync(timeout.Token);
            // The independent channel owns the command until its finite deadline,
            // including a gesture already executing when this page is left.
            var output = await action(channel, timeout.Token);
            if (!Current()) return;
            OutputText.Text = output;
            _session.AddActivity(activity);
        }
        catch (Exception error)
        {
            if (!Current()) return;
            OutputText.Text = "失败: " + error.Message;
            _session.AddActivity(activity + "失败", true);
        }
        finally
        {
            _busy = false; SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var button in ActionsPanel.Children.OfType<Button>()) button.IsEnabled = !busy;
        Progress.IsActive = busy;
    }
}
