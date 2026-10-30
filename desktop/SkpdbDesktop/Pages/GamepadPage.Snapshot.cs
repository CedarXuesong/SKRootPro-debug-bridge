using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SkpdbDesktop;

public sealed partial class GamepadPage
{
    private CancellationTokenSource? _snapshotCapture;
    private ScreenSnapshot? _snapshot;
    private SoftwareBitmapSource? _snapshotImage;
    private Image? _snapshotBackground;
    private long _snapshotGeneration;
    private long _captureGeneration;
    private bool _snapshotWasAligned;

    private bool SnapshotAligned => _snapshot is { } snapshot && !_developerPreviewAccess &&
        _session.IsConnected && _session.Client.ConnectionGeneration == _snapshotGeneration &&
        (_device is null || (_device.Rotation == snapshot.Rotation &&
            _device.DisplayWidth == snapshot.Width && _device.DisplayHeight == snapshot.Height));

    private async void CaptureSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (!_pageActive || !_session.IsConnected || _developerPreviewAccess || _snapshotCapture is not null) return;
        using var request = new CancellationTokenSource();
        _snapshotCapture = request;
        _captureGeneration = _session.Client.ConnectionGeneration;
        UpdateSnapshotUi();
        try
        {
            var snapshot = await ScreenSnapshotCapture.CaptureAsync(_session.Client, request.Token);
            if (!CaptureStillCurrent(request)) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(snapshot.Png);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth != snapshot.Width || decoder.PixelHeight != snapshot.Height)
                throw new InvalidDataException("屏幕快照解码尺寸与图片头不一致");
            // BitmapImage may defer pixel decoding and accept a damaged IDAT.
            // Decode all pixels before publishing a replacement frame.
            using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var image = new SoftwareBitmapSource();
            await image.SetBitmapAsync(pixels);
            if (!CaptureStillCurrent(request)) return;
            // Keep the previous valid frame until capture and decoding both succeed.
            _snapshot = snapshot with { Png = Array.Empty<byte>() };
            _snapshotImage = image;
            _snapshotGeneration = _captureGeneration;
            SnapshotVisibleToggle.IsChecked = true;
            await RefreshDisplayAsync();
            if (!CaptureStillCurrent(request)) return;
            DrawLayout();
            SetStatus($"已获取 {snapshot.CapturedAt:HH:mm:ss.fff} 屏幕快照 · {snapshot.Width}×{snapshot.Height} ");
        }
        catch (OperationCanceledException)
        {
            if (CaptureStillCurrent(request)) SetStatus("获取快照超时，请重试；上一张有效快照已保留", true);
        }
        catch (Exception error)
        {
            if (CaptureStillCurrent(request))
            {
                LogPageError("CaptureSnapshot", error);
                SetStatus("获取快照失败：" + error.Message, true);
            }
        }
        finally
        {
            if (ReferenceEquals(_snapshotCapture, request))
            {
                _snapshotCapture = null;
                UpdateSnapshotUi();
            }
        }
    }

    private bool CaptureStillCurrent(CancellationTokenSource request) =>
        _pageActive && !request.IsCancellationRequested && ReferenceEquals(_snapshotCapture, request) &&
        _session.IsConnected && _session.Client.ConnectionGeneration == _captureGeneration;

    private void SnapshotVisible_Changed(object sender, RoutedEventArgs e)
    {
        if (LayoutCanvas is null || SnapshotStatusText is null) return;
        UpdateSnapshotUi();
        DrawLayout();
    }

    private void ClearSnapshot_Click(object sender, RoutedEventArgs e)
    {
        ReleaseSnapshot();
        SetStatus("已清除内存中的屏幕快照");
    }

    private void ReleaseSnapshot()
    {
        _snapshotCapture?.Cancel();
        _snapshotCapture = null;
        _snapshot = null;
        _snapshotImage = null;
        if (_snapshotBackground is not null) _snapshotBackground.Source = null;
        _snapshotBackground = null;
        _snapshotWasAligned = false;
        // DrawLayout clears the Image child too; cached pages must not retain its source.
        DrawLayout();
        UpdateSnapshotUi();
    }

    private void UpdateSnapshotUi()
    {
        if (CaptureSnapshotButton is null) return;
        var generation = _session.Client.ConnectionGeneration;
        if ((_snapshot is not null && (!_session.IsConnected || _snapshotGeneration != generation)) ||
            (_snapshotCapture is not null && (!_session.IsConnected || _captureGeneration != generation)))
        {
            ReleaseSnapshot();
            return;
        }
        var busy = _snapshotCapture is not null;
        CaptureSnapshotButton.IsEnabled = _pageActive && _session.IsConnected && !_developerPreviewAccess && !busy;
        CaptureSnapshotButton.Content = busy ? "获取中…" : "获取快照";
        SnapshotVisibleToggle.IsEnabled = _snapshot is not null;
        ClearSnapshotButton.IsEnabled = _snapshot is not null || busy;
        var aligned = SnapshotAligned;
        SnapshotStatusText.Text = busy ? "正在获取手机屏幕…" : _snapshot is { } snapshot
            ? $"{snapshot.CapturedAt:HH:mm:ss.fff} · {snapshot.Width}×{snapshot.Height}" +
              (!aligned ? " · 方向已改变，请重新获取" : SnapshotVisibleToggle.IsChecked != true ? " · 已隐藏" : " · 内存快照")
            : "未获取快照";
        ToolTipService.SetToolTip(SnapshotStatusText, _snapshot is { } info
            ? $"采集于 {info.CapturedAt:yyyy-MM-dd HH:mm:ss.fff zzz}\n快照 {info.Id}"
            : "未获取快照");
        if (_snapshotWasAligned != aligned)
        {
            _snapshotWasAligned = aligned;
            DrawLayout();
        }
    }

    private void DrawSnapshotBackground()
    {
        if (_snapshotImage is null || SnapshotVisibleToggle.IsChecked != true || !SnapshotAligned) return;
        var image = new Image
        {
            Source = _snapshotImage,
            Width = _screenRect.Width,
            Height = _screenRect.Height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(image, _screenRect.X);
        Canvas.SetTop(image, _screenRect.Y);
        LayoutCanvas.Children.Add(image);
        _snapshotBackground = image;
    }
}
