using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SkpdbDesktop;

public sealed partial class HomePage : Page
{
    private readonly AppSession _session = AppSession.Current;
    private bool _connecting;

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
        PageScroll.Loaded += (_, _) =>
        {
            LayoutContent();
            DispatcherQueue.TryEnqueue(() => PageScroll.ChangeView(null, 0, null, true));
        };
        PageScroll.SizeChanged += (_, _) => LayoutContent();
        ContentColumn.SizeChanged += (_, _) => LayoutContent();
    }

    private void LayoutContent()
    {
        var available = PageScroll.ViewportWidth;
        if (available <= 0)
        {
            available = PageScroll.ActualWidth;
        }
        ContentColumn.Width = Math.Max(320, available - 64);
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = SettingsStore.Load();
        if (string.IsNullOrEmpty(HostBox.Text))
        {
            HostBox.Text = settings.Host;
            PortBox.Text = settings.Port.ToString();
        }

        _session.Changed += Session_Changed;
        ActivityList.ItemsSource = _session.Activity;
        UpdateUi();
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _session.Changed -= Session_Changed;
    }

    private void Session_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateUi);
    }

    private void UpdateUi()
    {
        var connected = _session.IsConnected;
        var settings = SettingsStore.Load();
        ConnectionHint.Text =
            $"knock {settings.KnockPort} · 超时 {settings.TimeoutSeconds}s";
        StatusText.Text = _connecting ? "连接中…" : connected ? "已连接" : "未连接";
        StatusDot.Fill = new SolidColorBrush(connected ? Colors.LightGreen : Colors.Gray);
        ConnectButton.Content = _connecting ? "连接中…" : connected ? "断开" : "连接";
        ConnectButton.IsEnabled = !_connecting;
        EmptyActivityText.Visibility =
            _session.Activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var info = _session.Info;
        ModuleNameText.Text = info?.Name is { Length: > 0 } name ? name : "—";
        VersionText.Text = info?.Version is { Length: > 0 } version ? version : "—";
        PidText.Text = info is null
            ? "—"
            : $"{info.Pid} · {HumanUptime(info.UptimeSeconds)}";
        PortsText.Text = info is null
            ? "—"
            : $"{info.ServicePort} / knock {info.KnockPort}";
        SessionsText.Text = info is null
            ? "—"
            : $"{info.Sessions} / {info.Peers}";
        MemoryText.Text = info?.MemoryBackends is { Length: > 0 } backends ? backends : "—";
    }

    private static string HumanUptime(long seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds} 秒";
        }
        if (seconds < 3600)
        {
            return $"{seconds / 60} 分 {seconds % 60} 秒";
        }
        return $"{seconds / 3600} 小时 {(seconds % 3600) / 60} 分";
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        => await ConnectAsync();

    private async Task ConnectAsync()
    {
        if (_connecting) return;
        if (_session.IsConnected)
        {
            _session.Client.Disconnect();
            _session.Info = null;
            _session.AddActivity("已断开连接");
            UpdateUi();
            return;
        }

        var endpoint = ParseEndpoint(HostBox.Text, 0);
        if (string.IsNullOrWhiteSpace(endpoint.Host))
        {
            ShowError("请输入设备地址");
            return;
        }
        if (string.IsNullOrEmpty(KeyBox.Password))
        {
            ShowError("请输入 Root 密钥");
            return;
        }
        var port = endpoint.Port;
        if (port == 0 && (!int.TryParse(PortBox.Text, out port) || port is < 1 or > 65535))
        {
            ShowError("端口无效");
            return;
        }

        HostBox.Text = endpoint.Host;
        PortBox.Text = port.ToString();
        var saved = SettingsStore.Load();
        var options = new ConnectionOptions(
            endpoint.Host, port, saved.KnockPort, KeyBox.Password.Trim(), saved.TimeoutSeconds);
        _connecting = true;
        UpdateUi();
        ErrorBar.IsOpen = false;
        var generation = _session.Client.ConnectionGeneration;
        try
        {
            _session.Info = null;
            var attempt = _session.Client.ConnectAsync(options);
            generation = _session.Client.ConnectionGeneration;
            await attempt;
            if (generation != _session.Client.ConnectionGeneration) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120)));
            using var channel = await _session.Client.OpenIndependentChannelAsync(timeout.Token);
            var info = await channel.GetInfoAsync(timeout.Token);
            if (generation != _session.Client.ConnectionGeneration) return;
            _session.Info = info;
            _session.Options = options with { Key = "" };
            _session.AddActivity($"已连接 {options.Host}");
            saved.Host = options.Host;
            saved.Port = options.Port;
            if (!SettingsStore.Save(saved)) ShowError("已连接，但无法保存连接设置：" + SettingsStore.LastError);
        }
        catch (Exception error)
        {
            // A failed old attempt must never close or overwrite a newer session.
            if (generation != _session.Client.ConnectionGeneration) return;
            _session.Client.Disconnect();
            _session.Info = null;
            _session.AddActivity("连接失败", true);
            ShowError(error.Message);
        }
        finally
        {
            _connecting = false;
            UpdateUi();
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string tag)
        {
            App.MainWindow?.Navigate(tag);
        }
    }

    /// <summary>
    /// Accepts "host", "host:port", "http://host:port/path" and "[::1]:port".
    /// A port written inside the address wins over the separate port box.
    /// </summary>
    private static (string Host, int Port) ParseEndpoint(string text, int fallbackPort)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            return (value, fallbackPort);
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            value = value[(scheme + 3)..];
        var slash = value.IndexOfAny(['/', '\\']);
        if (slash >= 0)
            value = value[..slash];

        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var end = value.IndexOf(']');
            if (end > 0)
            {
                var host = value[1..end];
                var rest = value[(end + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var bracketed) &&
                    bracketed is > 0 and <= 65535)
                    return (host, bracketed);
                return (host, fallbackPort);
            }
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon &&
            int.TryParse(value[(colon + 1)..], out var parsed) && parsed is > 0 and <= 65535)
            return (value[..colon], parsed);
        return (value, fallbackPort);
    }
}
