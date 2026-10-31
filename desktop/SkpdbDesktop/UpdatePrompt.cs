using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace SkpdbDesktop;

public static class UpdatePrompt
{
    private static bool _checking;

    public static async Task CheckAsync(XamlRoot root, bool interactive = false)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            AvailableUpdate? update;
            try
            {
                update = await new UpdateChecker().CheckAsync(
                    BuildInfo.Channel, BuildInfo.DisplayVersion, BuildInfo.Commit);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or
                                          System.Text.Json.JsonException or InvalidDataException)
            {
                if (interactive)
                    await ShowMessageAsync(root, "检查更新失败", "暂时无法连接更新服务，请稍后重试。");
                return;
            }
            if (update is null)
            {
                if (interactive)
                    await ShowMessageAsync(root, "检查更新", $"当前已是最新 {BuildInfo.Channel} 版。");
                return;
            }
            var dialog = CreateUpdateDialog(root, update);
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (!await Launcher.LaunchUriAsync(update.ReleaseUrl))
                    await ShowMessageAsync(root, "无法打开浏览器", "请从 GitHub 仓库的 Releases 页面下载更新。");
            }
        }
        catch (Exception error)
        {
            // Closing the window or another modal dialog must not interrupt normal use.
            BufferedLog.Write("Update check: " + error.GetType().Name);
        }
        finally { _checking = false; }
    }

    private static ContentDialog CreateUpdateDialog(XamlRoot root, AvailableUpdate update) => new()
    {
        XamlRoot = root,
        Title = $"发现新的 {BuildInfo.Channel} 版本",
        Content = $"当前版本：{BuildInfo.DisplayVersion}\n最新版本：{update.Version}",
        PrimaryButtonText = "前往更新",
        CloseButtonText = "稍后",
        DefaultButton = ContentDialogButton.Primary
    };

    private static async Task ShowMessageAsync(XamlRoot root, string title, string content) =>
        await new ContentDialog { XamlRoot = root, Title = title, Content = content, CloseButtonText = "确定" }.ShowAsync();
}
