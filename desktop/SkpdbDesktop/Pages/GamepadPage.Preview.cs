using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace SkpdbDesktop;

public sealed partial class GamepadPage
{
    private LocalInputPreview? _canvasPreview;
    private TouchDeviceInfo? _previewDevice;
    private GamepadProfile? _previewProfile;
    private readonly Stopwatch _previewClock = new();
    private double _previewNextSample, _previewMouseX, _previewMouseY, _previewStickX, _previewStickY;
    private uint? _previewPointer;
    private bool _previewDraggingStick, _changingPreviewOrientation;
    private Point _previewLastPointer;
    private string? _previewButtonId;
    private GamepadMoveSettings? _previewPointerMove;
    private readonly HashSet<string> _previewPendingClicks = new();
    private long _previewSkipped;
    private string _previewAction = "";
    private const double PreviewStep = 1000.0 / SampledJoystick.SampleRate;

    private async Task ToggleCanvasPreviewAsync()
    {
        if (_canvasPreview is not null) { StopCanvasPreview(); return; }
        if (_profile is null) return;
        if (_engine.IsRunning) await StopEngineAsync();
        _previewProfile = _profile.Clone();
        _previewProfile.Validate();
        if (_editorMode) { _editorMode = false; ApplyModeLayout(); }
        PreviewAutoToggle.IsChecked = true;
        _changingPreviewOrientation = true;
        PreviewLandscapeToggle.IsChecked = _profile.ScreenWidth < _profile.ScreenHeight;
        _changingPreviewOrientation = false;
        PreviewAutoToggle.Visibility = PreviewLandscapeToggle.Visibility = Visibility.Visible;
        StartCanvasPreview();
        InputSurface.Focus(FocusState.Programmatic); UpdateMetrics();
    }

    private void StartCanvasPreview()
    {
        if (_previewProfile is null) return;
        if (_canvasPreview is not null)
        {
            _canvasPreview.ReleaseAll(); _canvasPreview.Trace.Drain();
            LayoutCanvas.ReleasePointerCaptures();
        }
        var source = new TouchDeviceInfo("preview", "", "",
            _previewProfile.ScreenWidth, _previewProfile.ScreenHeight, 9, true, true);
        // Orientation affects only the local viewport and coordinate transform.
        var width = source.ScreenWidth; var height = source.ScreenHeight;
        var landscape = PreviewLandscapeToggle.IsChecked == true;
        var rotate = landscape != (width > height);
        _previewDevice = source.WithDisplay(rotate ? 1 : 0, rotate ? height : width, rotate ? width : height);
        _canvasPreview = new LocalInputPreview(_previewDevice, _previewProfile);
        _canvasPreview.Trace.Enabled = TraceToggle.IsChecked == true;
        _previewPointer = null; _previewButtonId = null; _previewPointerMove = null; _previewPendingClicks.Clear(); _previewDraggingStick = false;
        _previewNextSample = _previewMouseX = _previewMouseY = _previewStickX = _previewStickY = 0;
        _previewSkipped = 0; _traceLayout = null;
        DrawLayout();
        _previewClock.Restart(); _traceTimer.Start();
        RenderCanvasTrace(); UpdateCanvasPreviewStatus();
    }

    private void StopCanvasPreview()
    {
        if (_canvasPreview is null) return;
        _previewClock.Stop(); _canvasPreview.ReleaseAll();
        CanvasTraceView.Append(_canvasPreview.Trace.Drain());
        _canvasPreview = null; _previewDevice = null; _previewProfile = null;
        _previewPointer = null; _previewButtonId = null; _previewPointerMove = null; _previewPendingClicks.Clear(); LayoutCanvas.ReleasePointerCaptures();
        _previewMouseX = _previewMouseY = _previewStickX = _previewStickY = 0;
        PreviewAutoToggle.Visibility = PreviewLandscapeToggle.Visibility = Visibility.Collapsed;
        if (TraceToggle.IsChecked != true) _traceTimer.Stop();
        _traceLayout = null; DrawLayout(); SyncPlayModeUi(force: true);
        UpdateMetrics(); UpdateConnectionState();
    }

    private void PauseCanvasPreview()
    {
        if (_canvasPreview is null) return;
        _previewClock.Stop(); _canvasPreview.ReleaseAll();
        CanvasTraceView.Append(_canvasPreview.Trace.Drain());
        _previewPointer = null; _previewButtonId = null; _previewPointerMove = null; _previewPendingClicks.Clear(); LayoutCanvas.ReleasePointerCaptures();
        _previewMouseX = _previewMouseY = _previewStickX = _previewStickY = 0;
        UpdateCanvasPreviewStatus();
    }

    private void ResumeCanvasPreview()
    {
        if (_canvasPreview is null) return;
        _previewClock.Start(); _traceTimer.Start(); UpdateCanvasPreviewStatus();
    }

    private void PreviewAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (_canvasPreview is null) return;
        // Switching the source leaves the sampled contact and all derivatives
        // intact; the next sample retargets from its current state.
        InputSurface.Focus(FocusState.Programmatic); UpdateCanvasPreviewStatus();
    }

    private void PreviewOrientation_Changed(object sender, RoutedEventArgs e)
    {
        if (_changingPreviewOrientation || _canvasPreview is null) return;
        StartCanvasPreview(); InputSurface.Focus(FocusState.Programmatic);
    }

    private void RenderCanvasTrace()
    {
        if (_canvasPreview is not { } preview)
        {
            var actual = _engine.TouchTrace.Drain();
            if (actual.Length > 0) CanvasTraceView.Append(actual);
            return;
        }
        if (!_previewClock.IsRunning || !IsAppActive()) return;
        var end = _previewClock.Elapsed.TotalMilliseconds;
        var count = Math.Max(0, (int)Math.Floor((end - _previewNextSample) / PreviewStep) + 1);
        if (count > 240)
        { _previewSkipped += count - 240; _previewNextSample += (count - 240) * PreviewStep; count = 240; }
        var keyX = (IsBindingDown("D") ? 1 : 0) - (IsBindingDown("A") ? 1 : 0);
        var keyY = (IsBindingDown("S") ? 1 : 0) - (IsBindingDown("W") ? 1 : 0);
        var lookX = (IsBindingDown("Right") ? 1 : 0) - (IsBindingDown("Left") ? 1 : 0);
        var lookY = (IsBindingDown("Down") ? 1 : 0) - (IsBindingDown("Up") ? 1 : 0);
        if (keyX != 0 || keyY != 0 || lookX != 0 || lookY != 0) PreviewAutoToggle.IsChecked = false;
        for (var i = 0; i < count; i++, _previewNextSample += PreviewStep)
        {
            var x = _previewDraggingStick ? _previewStickX : keyX;
            var y = _previewDraggingStick ? _previewStickY : keyY;
            var dx = _previewMouseX / (count - i); var dy = _previewMouseY / (count - i);
            _previewMouseX -= dx; _previewMouseY -= dy;
            dx += lookX * 2.8; dy += lookY * 2.8;
            var holdLook = (_previewPointer is not null && !_previewDraggingStick && _previewButtonId is null) || lookX != 0 || lookY != 0;
            _previewAction = "手动输入";
            if (PreviewAutoToggle.IsChecked == true)
            {
                (x, y, _previewAction) = (_previewNextSample % 3000) switch
                {
                    < 420 => (1d, 0d, "向右推出"),
                    < 720 => (0d, -1d, "微弧转上"),
                    < 810 => (-1d, 0d, "中途打断 · 向左"),
                    < 840 => (0d, 0d, "原地抬指"),
                    < 1160 => (1d, -1d, "新手势 · 右上"),
                    < 1520 => (0d, 1d, "反向转下"),
                    < 1860 => (-1d, 1d, "微弧转左下"),
                    < 2200 => (1d, 0d, "微弧回右"),
                    _ => (0d, 0d, "原地抬指")
                };
                var t = _previewNextSample % 7000;
                if (t < 5800) { dx += 2.8 * Math.Cos(t / 850); dy += 1.5 * Math.Sin(t / 600); holdLook = true; }
            }
            var buttons = _previewProfile!.Buttons.Where(b => IsBindingDown(b.Binding) || b.Id == _previewButtonId || _previewPendingClicks.Contains(b.Id)).Select(b => b.Id).ToHashSet();
            preview.Step(_previewNextSample, x, y, IsBindingDown("LeftShift") || IsBindingDown("RightShift"), dx, dy, holdLook, pressedButtons: buttons);
            _previewPendingClicks.Clear();
        }
        var points = preview.Trace.Drain();
        if (points.Length > 0) CanvasTraceView.Append(points);
    }

    private void UpdateCanvasPreviewStatus()
    {
        if (_canvasPreview is not { } preview) return;
        ModeBadge.Visibility = Visibility.Visible;
        SubtitleText.Text = "本地预览";
        ModeBadgeText.Text = _previewClock.IsRunning ? "本地预览" : "预览已暂停";
        ModeBadge.Background = new SolidColorBrush(Colors.SteelBlue);
        HintText.Text = "";
        ToolTipService.SetToolTip(HintText, HintText.Text);
        RttText.Text = $"触点 {preview.Slots.ActiveCount}/{preview.Slots.Capacity} · 等待 {preview.Slots.PendingCount} · 本地 {preview.Samples} 帧 · 固定步长 240 Hz";
        ToolTipService.SetToolTip(RttText, $"界面约 60 Hz，跳过 {_previewSkipped} 帧");
        SetStatus(_previewClock.IsRunning
            ? $"本地预览 · {_previewAction} · 轮盘 {preview.ActiveMoveIndex + 1} / 视角 {preview.ActiveLookIndex + 1} · 绿=落指 / 白=当前 / 橙=抬指"
            : "本地预览已暂停 · 窗口失焦，触点已释放");
    }

    private bool HandlePreviewKeyDown(KeyRoutedEventArgs e)
    {
        if (_canvasPreview is null) return false;
        if (e.Key == VirtualKey.Escape) { StopCanvasPreview(); e.Handled = true; return true; }
        if (e.Key is VirtualKey.W or VirtualKey.A or VirtualKey.S or VirtualKey.D or
            VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        { PreviewAutoToggle.IsChecked = false; e.Handled = true; return true; }
        return false;
    }

    private bool PreviewPointerPressed(PointerRoutedEventArgs e)
    {
        if (_canvasPreview is null || _previewProfile is null || _previewPointer is not null) return false;
        var p = e.GetCurrentPoint(LayoutCanvas);
        if (!_screenRect.Contains(p.Position)) return false;
        RenderCanvasTrace(); PreviewAutoToggle.IsChecked = false;
        _previewButtonId = HitTestControl(p.Position)?.Id;
        if (_previewButtonId is not null) _previewPendingClicks.Add(_previewButtonId);
        _previewPointerMove = _previewProfile.Moves.Reverse().FirstOrDefault(move =>
        {
            var center = new Point(_screenRect.X + move.CenterX * _screenRect.Width, _screenRect.Y + move.CenterY * _screenRect.Height);
            return move.Enabled && Distance(center, p.Position) <= CanvasMoveRadius(move) * (1 + move.SprintExtra) + 5;
        });
        _previewDraggingStick = _previewButtonId is null && !p.Properties.IsRightButtonPressed && _previewPointerMove is not null;
        _previewPointer = e.Pointer.PointerId; _previewLastPointer = p.Position;
        if (_previewDraggingStick) ReadPreviewStick(p.Position);
        LayoutCanvas.CapturePointer(e.Pointer); InputSurface.Focus(FocusState.Pointer); e.Handled = true;
        return true;
    }

    private void ReadPreviewStick(Point p)
    {
        if (_previewProfile is null) return;
        var move = _previewPointerMove ?? _canvasPreview?.Move.Settings ?? _previewProfile.Move;
        var radius = Math.Max(1, CanvasMoveRadius(move));
        _previewStickX = (p.X - _screenRect.X - move.CenterX * _screenRect.Width) / radius;
        _previewStickY = (p.Y - _screenRect.Y - move.CenterY * _screenRect.Height) / radius;
    }

    private void ReadPreviewPointer(Point p)
    {
        if (_previewDraggingStick) ReadPreviewStick(p);
        else if (_previewButtonId is null && _previewDevice is { } device)
        {
            var scale = device.DisplayWidth / Math.Max(1, _screenRect.Width);
            _previewMouseX += (p.X - _previewLastPointer.X) * scale;
            _previewMouseY += (p.Y - _previewLastPointer.Y) * scale;
        }
        _previewLastPointer = p;
    }

    private bool PreviewPointerMoved(PointerRoutedEventArgs e)
    {
        if (_canvasPreview is null || _previewPointer != e.Pointer.PointerId) return false;
        ReadPreviewPointer(e.GetCurrentPoint(LayoutCanvas).Position); e.Handled = true; return true;
    }

    private bool PreviewPointerReleased(PointerRoutedEventArgs e)
    {
        if (_canvasPreview is null || _previewPointer != e.Pointer.PointerId) return false;
        ReadPreviewPointer(e.GetCurrentPoint(LayoutCanvas).Position);
        RenderCanvasTrace();
        _previewPointer = null; _previewButtonId = null; _previewPointerMove = null; _previewDraggingStick = false; _previewStickX = _previewStickY = 0;
        LayoutCanvas.ReleasePointerCapture(e.Pointer); e.Handled = true; return true;
    }

    private void PreviewPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_previewPointer != e.Pointer.PointerId) return;
        _previewPointer = null; _previewButtonId = null; _previewPointerMove = null; _previewDraggingStick = false; _previewStickX = _previewStickY = 0;
    }

    private void PreviewPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        PreviewPointerCaptureLost(sender, e);
        _previewMouseX = _previewMouseY = 0;
    }
}
