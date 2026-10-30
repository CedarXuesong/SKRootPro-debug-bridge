using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace SkpdbDesktop;

public sealed partial class MemoryPage : Page
{
    private readonly AppSession _session = AppSession.Current;
    private bool _pageActive;
    private int _requestVersion, _busyCount;

    public MemoryPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    { base.OnNavigatedTo(e); _pageActive = true; }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    { _pageActive = false; ++_requestVersion; base.OnNavigatedFrom(e); }

    private async void MemoryInfoButton_Click(object sender, RoutedEventArgs e) => await ReadAsync(null);

    private async void MapsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!double.IsFinite(PidBox.Value) || PidBox.Value < 1 || PidBox.Value > 999999)
        { OutputText.Text = "请输入有效 PID。"; return; }
        await ReadAsync((int)PidBox.Value);
    }

    private async Task ReadAsync(int? pid)
    {
        if (!_pageActive) return;
        if (!_session.IsConnected)
        {
            OutputText.Text = "请先在主页连接设备。";
            return;
        }
        var generation = _session.Client.ConnectionGeneration; var version = ++_requestVersion;
        bool Current() => _pageActive && version == _requestVersion && generation == _session.Client.ConnectionGeneration;
        ++_busyCount; Progress.IsActive = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_session.Options?.TimeoutSeconds ?? 10, 1, 120)));
            using var channel = await _session.Client.OpenIndependentChannelAsync(timeout.Token);
            var output = pid is { } process ? await channel.MemoryMapsAsync(process, timeout.Token) : await channel.MemoryInfoAsync(timeout.Token);
            if (!Current()) return;
            OutputText.Text = output;
            _session.AddActivity(pid is not null ? $"读取 PID {pid} 的映射" : "读取内存后端信息");
        }
        catch (Exception error)
        {
            if (Current()) OutputText.Text = "读取失败: " + error.Message;
        }
        finally
        {
            Progress.IsActive = --_busyCount > 0;
        }
    }
}
