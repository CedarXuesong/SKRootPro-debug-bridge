using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SkpdbDesktop;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        VersionText.Text = $"SKPDB {BuildInfo.DisplayVersion}";
        Loaded += (_, _) =>
        {
            var settings = SettingsStore.Load();
            KnockPortBox.Value = settings.KnockPort;
            TimeoutBox.Value = settings.TimeoutSeconds;
            ThemeToggle.IsOn = settings.DarkTheme;
        };
    }

    private void OpenOfflinePreview_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window) return;
        window.OfflinePreviewRequested = true;
        window.Navigate("gamepad");
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        try { await UpdatePrompt.CheckAsync(XamlRoot, interactive: true); }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!double.IsFinite(KnockPortBox.Value) || KnockPortBox.Value is < 1 or > 65535 ||
            !double.IsFinite(TimeoutBox.Value) || TimeoutBox.Value is < 1 or > 120)
        { StatusText.Text = "请输入有效端口（1–65535）和超时（1–120 秒）。"; return; }
        var settings = SettingsStore.Load();
        settings.KnockPort = (int)KnockPortBox.Value;
        settings.TimeoutSeconds = (int)TimeoutBox.Value;
        settings.DarkTheme = ThemeToggle.IsOn;
        if (!SettingsStore.Save(settings)) { StatusText.Text = "保存失败：" + SettingsStore.LastError; return; }
        App.MainWindow?.ApplyTheme(settings.DarkTheme);
        StatusText.Text = "已保存";
        AppSession.Current.AddActivity("保存设置");
    }
}
