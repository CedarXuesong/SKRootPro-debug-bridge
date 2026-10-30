using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;

namespace SkpdbDesktop;

public sealed partial class GamepadPage
{
    private void OnNativeMouseButton(string button, bool down, int screenX, int screenY)
    {
        // One owner per physical press. XAML never injects the same input again.
        if (down)
        {
            if (_editorMode && _capturingBinding && IsAppActive())
            {
                _suppressCanvasPressUntil = DateTime.UtcNow.AddMilliseconds(400);
                DispatcherQueue.TryEnqueue(() => ApplyCapturedBinding(button));
                return;
            }
            if (_editorMode || _capturingBinding || !_engine.IsRunning || !IsAppActive() ||
                DateTime.UtcNow <= _suppressCanvasPressUntil || !_nativeMouseButtons.Add(button)) return;
            if (!_engine.IsMouseMode)
            {
                if (_mouseCapture.IsActive) _engine.SetMouseButton(button, true);
                return;
            }
            if (button != "MouseLeft") return;
            // Engine mode changes precede their queued UI notification. Refresh
            // mode and geometry before routing the first click, also recovering
            // a capture that failed transiently during rotation or activation.
            UpdateMouseCapture(viewMode: false);
            if (!_mouseCapture.TryNormalizeScreenPoint(screenX, screenY, out var x, out var y)) return;
            if (!_engine.DirectTouchAvailable)
            {
                DispatcherQueue.TryEnqueue(() => SetStatus("直接触摸尚未初始化，请重新启动操控。", true));
                return;
            }
            _directTouchActive = true;
            _engine.PointerTouchDown(x, y);
        }
        else
        {
            _nativeMouseButtons.Remove(button);
            if (button == "MouseLeft" && _directTouchActive)
            {
                if (_mouseCapture.TryGetNormalizedCursor(out var x, out var y)) _engine.PointerTouchUp(x, y);
                else _engine.PointerTouchUp();
                _directTouchActive = false;
            }
            _engine.SetMouseButton(button, false);
        }
        QueueNativePointerUpdate();
    }

    private void QueueNativePointerUpdate()
    {
        if (_editorMode || !_engine.IsRunning || _nativePointerUpdateQueued)
            return;
        _nativePointerUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _nativePointerUpdateQueued = false;
            if (_editorMode || !_engine.IsRunning || !IsAppActive())
                return;
            _mouseCapture.EnsureCapture();
            if (!_mouseCapture.TryGetNormalizedCursor(out var x, out var y))
                return;
            var position = new Point(_screenRect.X + x * _screenRect.Width,
                _screenRect.Y + y * _screenRect.Height);
            if (_rawMouse is not { IsRegistered: true } && _hasPointer &&
                _engine.IsLooking)
                _engine.AddMouseDelta(position.X - _lastPointer.X, position.Y - _lastPointer.Y);
            _lastPointer = position;
            _hasPointer = true;
            UpdateCursor(position, !_engine.IsLooking);
        }))
            _nativePointerUpdateQueued = false;
    }

    private void OnNativeMouseWheel(int delta)
    {
        if (_editorMode || !_engine.IsRunning || !_engine.IsMouseMode || !IsAppActive() ||
            !_mouseCapture.TryGetNormalizedCursor(out var x, out var y))
            return;
        var position = new Point(_screenRect.X + x * _screenRect.Width,
            _screenRect.Y + y * _screenRect.Height);
        if (HitTestControl(position) is { Kind: GamepadControlKind.Scroll or GamepadControlKind.Zoom } control)
            _engine.PulseControl(control.Id, delta);
    }

    private void ReleaseNativeMouseInput()
    {
        EndDirectTouch();
        foreach (var button in _nativeMouseButtons)
            _engine.SetMouseButton(button, false);
        _nativeMouseButtons.Clear();
    }

    private bool OnGlobalKey(int vkCode, bool down)
    {
        // Always deliver accepted releases, including releases over another app.
        if (!down)
        {
            var owned = _nativeKeys.Remove(vkCode);
            if (owned) _engine.SetKey(KeyName((VirtualKey)vkCode), false);
            return owned && _pageActive && _engine.IsRunning && !_editorMode && IsAppActive() && vkCode is 0x09 or 0x1B;
        }
        if (!_pageActive || !_engine.IsRunning || _editorMode || !IsAppActive()) return false;
        // Leave system task switching intact. Modifier DOWN must also reach
        // Windows, otherwise passing only the later Tab cannot restore Alt+Tab.
        if (vkCode == 0x09 && ((NativeKeyboard.GetAsyncKeyState(0x12) & 0x8000) != 0 ||
                (NativeKeyboard.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
                (NativeKeyboard.GetAsyncKeyState(0x5C) & 0x8000) != 0))
        {
            _engine.ClearInput();
            _engine.SetViewMode(false);
            return false;
        }
        var first = _nativeKeys.Add(vkCode);
        if (vkCode == 0x1B)
        {
            if (first) DispatcherQueue.TryEnqueue(HandleEscapeHook);
            return true;
        }
        if (first) _engine.SetKey(KeyName((VirtualKey)vkCode), true);
        return vkCode == 0x09;
    }

    private void HandleEscapeHook()
    {
        if (!_engine.IsMouseMode)
        {
            _engine.SetViewMode(false);
            _engine.ClearInput();
            SyncPlayModeUi(force: true);
            SetStatus("已退出视角模式：鼠标已释放");
            return;
        }
        if (_engine.IsRunning)
            _ = StopEngineAsync();
    }

    private void MainWindow_Activated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated)
        {
            PauseCanvasPreview();
            ReleaseNativeMouseInput();
            _engine.SetInputEnabled(false);
            _mouseCapture.Release();
            if (_engine.ViewMode)
            {
                // Never leave the user trapped in a hidden, clipped pointer when
                // they click another window; come back in normal mouse mode.
                _engine.SetViewMode(false);
                SyncPlayModeUi(force: true);
            }
            return;
        }
        _engine.SetInputEnabled(true);
        ResumeCanvasPreview();
        UpdateMouseCapture(!_engine.IsMouseMode);
    }

    /// <summary>
    /// While controlling, the pointer belongs to the phone screen: mouse mode
    /// clips the visible arrow to that rectangle, view mode hides it, pins it to
    /// the rectangle center and feeds Raw Input relative motion.
    /// </summary>
    private void UpdateMouseCapture(bool viewMode)
    {
        if (!_engine.IsRunning || _editorMode || !IsAppActive() ||
            App.MainWindow is null || _screenRect.Width <= 1 || _screenRect.Height <= 1)
        {
            _mouseCapture.Release();
            return;
        }

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var origin = LayoutCanvas.TransformToVisual(null)
                .TransformPoint(new Point(_screenRect.X, _screenRect.Y));
            _mouseCapture.Apply(hwnd, origin.X, origin.Y,
                _screenRect.Width, _screenRect.Height, XamlRoot.RasterizationScale, viewMode);
            if (viewMode)
            {
                _mouseCapture.KeepHidden();
            }
        }
        catch
        {
            _mouseCapture.Release();
        }
    }

    private void UpdateMouseCaptureClip() => UpdateMouseCapture(!_engine.IsMouseMode);

    private void OnRawMouseMoved(int dx, int dy)
    {
        if (!_engine.IsRunning || !IsAppActive())
        {
            return;
        }
        if (_engine.ViewMode && _mouseCapture.IsActive)
        {
            // WM_SETCURSOR can put the arrow back while the look finger moves.
            _mouseCapture.KeepHidden();
        }
        if (_engine.IsLooking)
        {
            _engine.AddMouseDelta(dx, dy);
        }
        QueueNativePointerUpdate();
    }

}
