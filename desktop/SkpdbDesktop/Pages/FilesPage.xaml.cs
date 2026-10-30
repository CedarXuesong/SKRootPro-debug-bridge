using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace SkpdbDesktop;

public sealed partial class FilesPage : Page
{
    private readonly AppSession _session = AppSession.Current;
    private string _path = "/sdcard";
    private bool _pageActive;
    private int _listVersion, _busyCount;

    public FilesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageActive = true;
        if (_session.IsConnected)
        {
            _ = RefreshAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _pageActive = false; ++_listVersion;
        base.OnNavigatedFrom(e);
    }

    private async Task RefreshAsync()
    {
        if (!_pageActive) return;
        if (!_session.IsConnected) { ShowError("请先在主页连接设备"); return; }
        var path = string.IsNullOrWhiteSpace(PathBox.Text) ? "/sdcard" : PathBox.Text.Trim();
        var generation = _session.Client.ConnectionGeneration;
        var version = ++_listVersion;
        _path = path; PathBox.Text = path; DirectoryTitle.Text = path;
        FileList.ItemsSource = null;
        SetBusy(true); ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var channel = await _session.Client.OpenIndependentChannelAsync(timeout.Token);
            var entries = await channel.ListAsync(path, timeout.Token);
            if (!_pageActive || version != _listVersion || generation != _session.Client.ConnectionGeneration) return;
            FileList.ItemsSource = entries;
            _session.AddActivity($"浏览 {path}");
        }
        catch (Exception error)
        {
            if (_pageActive && version == _listVersion && generation == _session.Client.ConnectionGeneration) ShowError(error.Message);
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busyCount += busy ? 1 : -1;
        Progress.IsActive = _busyCount > 0;
        UploadButton.IsEnabled = DownloadButton.IsEnabled = _busyCount == 0;
    }

    private void RequireConnection(long generation)
    {
        if (!_session.IsConnected || generation != _session.Client.ConnectionGeneration)
            throw new InvalidOperationException("连接已变化，请重新选择文件后重试。");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await RefreshAsync();
        }
    }

    private async void UpButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _path.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        _path = slash <= 0 ? "/" : path[..slash];
        PathBox.Text = _path;
        await RefreshAsync();
    }

    private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry entry || !entry.IsDirectory)
        {
            return;
        }
        _path = _path.TrimEnd('/') + "/" + entry.Name;
        PathBox.Text = _path;
        await RefreshAsync();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileEntry entry || entry.IsDirectory)
        {
            ShowError("请选择一个文件");
            return;
        }
        if (!_session.IsConnected)
        {
            ShowError("请先在主页连接设备");
            return;
        }

        var remotePath = RemotePath(entry.Name);
        var generation = _session.Client.ConnectionGeneration;
        SetBusy(true);
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = entry.Name,
                SuggestedStartLocation = PickerLocationId.Downloads
            };
            // FileSavePicker requires a concrete extension; "*" and "." are not
            // valid here and make the WinRT picker fail with E_INVALIDARG.
            var extension = Path.GetExtension(entry.Name);
            picker.FileTypeChoices.Add("文件",
                new List<string> { string.IsNullOrEmpty(extension) ? ".bin" : extension });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            RequireConnection(generation);
            using var channel = await _session.Client.OpenIndependentChannelAsync();
            RequireConnection(generation);
            await channel.DownloadFileAsync(remotePath, file.Path);
            _session.AddActivity($"已下载到 {file.Path}");
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsConnected)
        {
            ShowError("请先在主页连接设备");
            return;
        }

        var directory = _path;
        var generation = _session.Client.ConnectionGeneration;
        SetBusy(true);
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            RequireConnection(generation);
            using var channel = await _session.Client.OpenIndependentChannelAsync();
            RequireConnection(generation);
            await using (var input = await file.OpenStreamForReadAsync())
            {
                await channel.UploadFileAsync(input, directory.TrimEnd('/') + "/" + file.Name);
            }
            _session.AddActivity($"已上传 {file.Name}");
            if (_pageActive && _path == directory && generation == _session.Client.ConnectionGeneration) await RefreshAsync();
        }
        catch (Exception error)
        {
            ShowError(error.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string RemotePath(string name) => _path.TrimEnd('/') + "/" + name;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        _session.AddActivity(message, true);
    }
}
