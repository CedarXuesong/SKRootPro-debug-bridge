using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SkpdbDesktop;

public sealed partial class TerminalPage : Page
{
    private readonly AppSession _session = AppSession.Current;
    private bool _running, _pageActive;
    private int _pageVersion, _outputVersion;

    public TerminalPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_session.IsConnected)
            {
                OutputText.Text = "尚未连接设备。请先在主页连接。";
            }
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    { base.OnNavigatedTo(e); _pageActive = true; }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    { _pageActive = false; ++_pageVersion; base.OnNavigatedFrom(e); }

    private void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = RunCommandAsync();
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = RunCommandAsync();

    private async Task RunCommandAsync()
    {
        if (_running || !_pageActive) return;
        var command = CommandBox.Text.Trim();
        if (command.Length == 0)
        {
            return;
        }
        if (!_session.IsConnected)
        {
            Append("未连接设备。");
            return;
        }

        var generation = _session.Client.ConnectionGeneration;
        var pageVersion = _pageVersion; var outputVersion = _outputVersion;
        bool Current() => _pageActive && pageVersion == _pageVersion && outputVersion == _outputVersion &&
            generation == _session.Client.ConnectionGeneration;
        _running = true;
        CommandBox.Text = string.Empty;
        Append($"> {command}");
        Progress.IsActive = true;
        SendButton.IsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_session.Options?.TimeoutSeconds ?? 10, 1, 120)));
            using var channel = await _session.Client.OpenIndependentChannelAsync(timeout.Token);
            var result = await channel.ExecAsync(command, timeout.Token);
            if (!Current()) return;
            if (!string.IsNullOrEmpty(result.Output))
            {
                Append(result.Output.TrimEnd('\r', '\n'));
            }
            Append($"[exit {result.ExitCode}]");
        }
        catch (Exception error)
        {
            if (!Current()) return;
            Append($"[错误] {error.Message}");
            _session.AddActivity("命令失败", true);
        }
        finally
        {
            _running = false;
            Progress.IsActive = false;
            SendButton.IsEnabled = true;
            if (_pageActive && pageVersion == _pageVersion) CommandBox.Focus(FocusState.Programmatic);
        }
    }

    private void Append(string text)
    {
        if (OutputText.Text == "连接设备后，在这里输入命令。" ||
            OutputText.Text == "尚未连接设备。请先在主页连接。")
        {
            OutputText.Text = string.Empty;
        }
        OutputText.Text += text + Environment.NewLine;
        if (OutputText.Text.Length > 200_000)
        {
            OutputText.Text = OutputText.Text[^150_000..];
        }
        OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ++_outputVersion;
        OutputText.Text = _session.IsConnected ? string.Empty : "尚未连接设备。请先在主页连接。";
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(OutputText.Text);
        Clipboard.SetContent(package);
        _session.AddActivity("复制终端输出");
    }
}
