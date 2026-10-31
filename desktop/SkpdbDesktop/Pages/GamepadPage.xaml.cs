using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace SkpdbDesktop;

public sealed partial class GamepadPage : Page
{
    private readonly AppSession _session = AppSession.Current;
    private readonly GamepadEngine _engine = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _statsTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _traceTimer;
    private string? _traceLayout;

    private GamepadProfileCollection _collection = new();
    private GamepadProfile? _profile;
    private TouchDeviceInfo? _device;
    private ForegroundWatcher? _watcher;
    private bool _updatingUi;
    private readonly SemaphoreSlim _engineTransitions = new(1, 1);
    private bool _pageActive;
    private bool _pageReady;
    private bool _applyingEnvironment;
    private string? _pendingForeground;
    private bool _pendingDisplay;
    private bool _displayRefreshing;
    private int _pageEpoch, _environmentVersion;
    private long _knownConnectionGeneration = -1;
    private bool _knownConnected;
    private Task _pageTeardown = Task.CompletedTask;
    private Task? _touchInitialization;
    private CancellationTokenSource? _environmentCts;
    private DateTime _lastDisplayCheck = DateTime.MinValue;

    // Play mode.
    private bool _hasPointer;
    private bool _directTouchActive;
    private readonly HashSet<string> _nativeMouseButtons = new();
    private bool _nativePointerUpdateQueued;
    private bool? _lastViewMode;
    private bool _lastLooking;
    private readonly HashSet<int> _nativeKeys = new();
    private bool _inputAttached;
    private RawMouseInput? _rawMouse;
    private readonly MouseCapture _mouseCapture = new();

    // Editor mode.
    private bool _editorMode;
    private int _moveIndex, _lookIndex;
    private bool _developerPreviewAccess;
    private GamepadMoveSettings SelectedMove => _profile!.Moves.ElementAtOrDefault(_moveIndex) ?? _profile!.Move;
    private GamepadLookSettings SelectedLook => _profile!.Looks.ElementAtOrDefault(_lookIndex) ?? _profile!.Look;
    private double LogicalWidth => (_previewDevice ?? (_developerPreviewAccess ? null : _device))?.DisplayWidth ??
        (!_developerPreviewAccess && _snapshot is not null ? _snapshot.Width :
        (_developerPreviewAccess && PreviewLandscapeToggle.IsChecked == true ? Math.Max(_profile!.ScreenWidth, _profile.ScreenHeight) : _profile!.ScreenWidth));
    private double LogicalHeight => (_previewDevice ?? (_developerPreviewAccess ? null : _device))?.DisplayHeight ??
        (!_developerPreviewAccess && _snapshot is not null ? _snapshot.Height :
        (_developerPreviewAccess && PreviewLandscapeToggle.IsChecked == true ? Math.Min(_profile!.ScreenWidth, _profile.ScreenHeight) : _profile!.ScreenHeight));
    private double CanvasMoveRadius(GamepadMoveSettings move) =>
        SampledJoystick.FittedRadius(move, LogicalWidth, LogicalHeight) * _screenRect.Width / LogicalWidth;

    private string? _selection;
    private string? _selectedButtonId;
    private string? _dragKind;
    private bool _draggingLayout;
    private bool _dragUndoPushed;
    private bool _resizingLayout;
    private string? _resizeTarget;
    private Point _resizeCenter;
    private bool _capturingBinding;
    private DateTime _suppressCanvasPressUntil = DateTime.MinValue;
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();

    private Point _lastPointer;
    private Rect _screenRect;

    private SkpdbClient? _cursorClient;
    private ControlOverlaySession? _controlOverlay;
    private bool _startingControl, _controlActive, _controlCleanupQueued;
    private CancellationTokenSource? _cursorCts;
    private int _cursorEpoch;
    private bool _cursorUpdating;
    private (int X, int Y, bool Visible)? _pendingCursor;
    private bool _cursorActive;
    private bool _cursorVisible;
    private DateTime _lastCursorSent = DateTime.MinValue;

    public GamepadPage()
    {
        InitializeComponent();
        PreviewWatermarkLayer.Visibility = BuildInfo.IsCanary ? Visibility.Visible : Visibility.Collapsed;
        _engine.Changed += Engine_Changed;
        _engine.Log += (_, message) =>
        {
            LogPageEvent(message);
            DispatcherQueue.TryEnqueue(() => SetStatus(message, _engine.Metrics.LastError is not null));
        };
        _engine.Trace += (_, message) => LogPageEvent(message);
        _statsTimer = DispatcherQueue.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromMilliseconds(500);
        _statsTimer.IsRepeating = true;
        _statsTimer.Tick += (_, _) => UpdateMetrics();
        _statsTimer.Start();
        _traceTimer = DispatcherQueue.CreateTimer();
        _traceTimer.Interval = TimeSpan.FromMilliseconds(16);
        _traceTimer.Tick += (_, _) => RenderCanvasTrace();
        if (TraceToggle.IsChecked == true) _traceTimer.Start();
        foreach (var slider in new[] { MoveStartBox, MoveTargetBox, MoveAngularBox, MoveCurveBox, MoveDistortionBox, MoveSwayBox, SprintExtraBox })
        {
            var label = slider.Header?.ToString() ?? "";
            slider.ValueChanged += (_, args) => slider.Header = $"{label}  {args.NewValue:F3}";
        }
        BuildKindList();
        BuildModeList();
        InitializeEditor();
        _rawMouse = CreateRawMouse();
        if (_rawMouse is not null)
        {
            _rawMouse.Moved += OnRawMouseMoved;
        }
        LogPageEvent("raw input " + (_rawMouse is { IsRegistered: true } ? "ready" : "unavailable"));
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private static RawMouseInput? CreateRawMouse()
    {
        // Test hook: let the pointer-event fallback drive the look path when
        // synthetic mouse input cannot reach Raw Input.
        if (Environment.GetEnvironmentVariable("SKPDB_DISABLE_RAW") == "1")
            return null;
        return RawMouseInput.TryCreate();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_inputAttached || !_pageActive) return;
        _inputAttached = true;
        _mouseCapture.Release();
        _engine.PointerPositionProvider = () =>
            _mouseCapture.TryGetNormalizedCursor(out var x, out var y) ? (x, y) : null;
        NativeMouseHook.Install(OnNativeMouseButton, QueueNativePointerUpdate, OnNativeMouseWheel);
        NativeKeyboardHook.Install(OnGlobalKey);
        if (App.MainWindow is { } window)
        {
            window.Activated += MainWindow_Activated;
        }
        if (_rawMouse is null)
        {
            _rawMouse = CreateRawMouse();
            if (_rawMouse is not null)
            {
                _rawMouse.Moved += OnRawMouseMoved;
            }
            LogPageEvent("raw input " + (_rawMouse is { IsRegistered: true } ? "ready" : "unavailable"));
        }
        ApplyModeLayout();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_pageActive) return; // A delayed Unloaded may belong to the previous navigation.
        ReleaseSnapshot();
        StopCanvasPreview();
        _traceTimer.Stop(); _engine.TouchTrace.Enabled = false;
        DetachNativeInput();
    }

    private void DetachNativeInput()
    {
        if (!_inputAttached) return;
        _inputAttached = false;
        ReleaseNativeMouseInput();
        foreach (var key in _nativeKeys) _engine.SetKey(KeyName((VirtualKey)key), false);
        _nativeKeys.Clear();
        _engine.PointerPositionProvider = null;
        NativeMouseHook.Uninstall();
        NativeKeyboardHook.Uninstall();
        if (App.MainWindow is { } window) window.Activated -= MainWindow_Activated;
        _mouseCapture.Release();
        if (_rawMouse is not null)
        {
            _rawMouse.Moved -= OnRawMouseMoved;
            _rawMouse.Dispose(); _rawMouse = null;
        }
    }

    private void ApplyModeLayout()
    {
        var showProfiles = _editorMode && ProfilesToggle.IsChecked == true;
        ProfileColumn.Width = showProfiles ? new GridLength(200) : new GridLength(0);
        InspectorColumn.Width = _editorMode ? new GridLength(300) : new GridLength(0);
        ProfilePanel.Visibility = showProfiles ? Visibility.Visible : Visibility.Collapsed;
        ProfilesToggle.Visibility = _editorMode ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = _editorMode ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = _editorMode ? Visibility.Collapsed : Visibility.Visible;
        UndoButton.Visibility = _editorMode ? Visibility.Visible : Visibility.Collapsed;
        RedoButton.Visibility = _editorMode ? Visibility.Visible : Visibility.Collapsed;
        ResetLayoutButton.Visibility = _editorMode ? Visibility.Visible : Visibility.Collapsed;
        TouchInfoText.Visibility = _editorMode ? Visibility.Collapsed : Visibility.Visible;
        EditModeButton.Content = _editorMode ? "完成编辑" : "编辑布局";
        if (!_editorMode)
        {
            CancelBindingCapture();
            _selection = null;
            _selectedButtonId = null;
            PopulateProperties(null);
        }
        else
        {
            PopulateProperties(_selection, _selectedButtonId);
        }
        UpdateUndoButtons();
        UpdateEditorAffordances();
        UpdateHintText();
        DrawLayout();
        SyncPlayModeUi(force: true);
    }

    private async Task ToggleEditorAsync(bool enter)
    {
        CommitPropertyEdit();
        StopCanvasPreview();
        if (_engine.IsRunning)
        {
            await StopEngineAsync();
        }
        _editorMode = enter;
        ApplyModeLayout();
        if (_editorMode)
        {
            InputSurface.Focus(FocusState.Programmatic);
            SetStatus("编辑布局");
        }
        else
        {
            if (_developerPreviewAccess) { await ToggleCanvasPreviewAsync(); }
            else SetStatus("已保存");
        }
    }

    private async void EditModeButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleEditorAsync(!_editorMode);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageActive = true;
        _pageReady = false;
        var epoch = ++_pageEpoch;
        StartButton.IsEnabled = false;
        await _pageTeardown;
        if (!_pageActive || epoch != _pageEpoch) return;
        ResetEnvironment();
        _knownConnected = _session.IsConnected;
        _knownConnectionGeneration = _session.Client.ConnectionGeneration;
        _developerPreviewAccess = App.MainWindow?.OfflinePreviewRequested == true;
        if (App.MainWindow is { } previewWindow) previewWindow.OfflinePreviewRequested = false;
        try
        {
            _statsTimer.Start();
            if (TraceToggle.IsChecked == true) TraceToggle_Changed(TraceToggle, new RoutedEventArgs());
            _session.Changed += Session_Changed;
            _collection = GamepadProfileStore.Load();
            _profile = GamepadProfileStore.Find(_collection, _watcher?.Package);
            RefreshProfileList();
            SelectProfile(_profile);
            UpdateConnectionState();
            if (_session.IsConnected && !_developerPreviewAccess)
            {
                await InitializeTouchAsync();
                if (!_pageActive || epoch != _pageEpoch) return;
                StartWatcher();
            }
            Page_Loaded(this, new RoutedEventArgs());
            _pageReady = true;
            StartButton.IsEnabled = _developerPreviewAccess || (_session.IsConnected && _device is not null);
            ApplyModeLayout();
            if (GamepadProfileStore.LastError is { } loadError) SetStatus("部分布局加载失败：" + loadError, true);
            if (_developerPreviewAccess) await ToggleCanvasPreviewAsync();
        }
        catch (Exception error)
        {
            LogPageError("OnNavigatedTo", error);
            SetStatus(error.Message, true);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CommitPropertyEdit();
        _pageActive = false;
        _pageReady = false;
        ++_pageEpoch;
        ResetEnvironment();
        ReleaseSnapshot(); StopCanvasPreview(); DetachNativeInput();
        _pendingForeground = null; _pendingDisplay = false;
        _session.Changed -= Session_Changed;
        _statsTimer.Stop(); _traceTimer.Stop(); _engine.TouchTrace.Enabled = false;
        CancelBindingCapture();
        var watcher = _watcher; _watcher = null;
        if (watcher is not null) watcher.Changed -= Watcher_Changed;
        SaveCollection();
        _pageTeardown = StopPageAsync(watcher);
        base.OnNavigatedFrom(e);
    }

    private async Task StopPageAsync(ForegroundWatcher? watcher)
    {
        try
        {
            await StopEngineAsync();
            if (watcher is not null) await watcher.DisposeAsync();
        }
        catch (Exception error) { LogPageError("StopPageAsync", error); }
    }

    public async Task ShutdownAsync()
    {
        CommitPropertyEdit();
        _pageActive = _pageReady = false; ++_pageEpoch;
        ResetEnvironment();
        _session.Changed -= Session_Changed;
        _statsTimer.Stop(); _traceTimer.Stop();
        StopCanvasPreview(); ReleaseSnapshot(); DetachNativeInput();
        var watcher = _watcher; _watcher = null;
        if (watcher is not null) watcher.Changed -= Watcher_Changed;
        await _pageTeardown;
        try { await StopEngineAsync(); }
        finally
        {
            if (watcher is not null) await watcher.DisposeAsync();
            await _engine.DisposeAsync();
        }
    }

    private void ResetEnvironment()
    {
        ++_environmentVersion;
        _environmentCts?.Cancel(); _environmentCts?.Dispose();
        _environmentCts = _pageActive ? new CancellationTokenSource() : null;
        _touchInitialization = null; _displayRefreshing = false;
    }

    private bool EnvironmentCurrent(int version, long generation) => _pageActive &&
        version == _environmentVersion && _session.IsConnected &&
        generation == _session.Client.ConnectionGeneration;

    private void Session_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_pageActive || _developerPreviewAccess) return;
            var connected = _session.IsConnected;
            var generation = _session.Client.ConnectionGeneration;
            if (connected == _knownConnected && generation == _knownConnectionGeneration) return;
            _knownConnected = connected; _knownConnectionGeneration = generation;
            ResetEnvironment();
            _pageReady = false;
            var version = _environmentVersion;
            var watcher = _watcher; _watcher = null;
            if (watcher is not null) watcher.Changed -= Watcher_Changed;
            _device = null;
            UpdateConnectionState();
            try
            {
                await StopEngineAsync();
                if (watcher is not null) await watcher.DisposeAsync();
                if (!EnvironmentCurrent(version, generation)) return;
                await InitializeTouchAsync();
                if (EnvironmentCurrent(version, generation))
                {
                    StartWatcher(); _pageReady = true;
                    StartButton.IsEnabled = _device is not null;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { LogPageError("Session_Changed", error); }
        });
    }

    private void UpdateConnectionState()
    {
        var connected = _session.IsConnected;
        ConnectionDot.Fill = new SolidColorBrush(connected ? Colors.LightGreen : Colors.Gray);
        StartButton.IsEnabled = _developerPreviewAccess || (connected && _pageReady && _device is not null);
        UpdateSnapshotUi();
        if (!connected)
        {
            SetStatus("请先在主页连接设备");
        }
        else
        {
            SetStatus($"已连接 {_session.Options?.Host}");
        }
    }

    private Task InitializeTouchAsync() => _touchInitialization ??= InitializeTouchCoreAsync();

    private async Task InitializeTouchCoreAsync()
    {
        var version = _environmentVersion;
        var generation = _session.Client.ConnectionGeneration;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_environmentCts?.Token ?? default);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var channel = await _session.Client.OpenIndependentChannelAsync(timeout.Token);
            var info = await channel.TouchInfoAsync(timeout.Token);
            if (!EnvironmentCurrent(version, generation)) return;
            _device = TouchDeviceInfo.Parse(info);
            TouchInfoText.Text = $"{_device.ScreenWidth}×{_device.ScreenHeight} · slot 0..{_device.SlotMax}";
            ToolTipService.SetToolTip(TouchInfoText, $"触摸屏 {_device.Name} · {_device.EventPath}");
            StartButton.IsEnabled = _pageReady;
            await RefreshDisplayAsync();
        }
        catch (Exception error)
        {
            if (!EnvironmentCurrent(version, generation)) return;
            _device = null; TouchInfoText.Text = "不可用"; StartButton.IsEnabled = false;
            SetStatus(error is OperationCanceledException ? "读取触摸屏信息超时，可重新连接后重试。" : error.Message, true);
        }
        finally { if (version == _environmentVersion) _touchInitialization = null; }
    }

    private async Task RefreshDisplayAsync()
    {
        if (!_pageActive || _displayRefreshing || _device is null || !_session.IsConnected)
        {
            return;
        }
        _displayRefreshing = true;
        var version = _environmentVersion;
        var generation = _session.Client.ConnectionGeneration;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_environmentCts?.Token ?? default);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var channel = await _session.Client.OpenIndependentChannelAsync(timeout.Token);
            var result = await channel.ExecAsync("dumpsys display", timeout.Token);
            if (!EnvironmentCurrent(version, generation) || _device is null) return;
            var display = TouchDeviceInfo.ParseDisplay(result.Output);
            if (display is not { } info)
            {
                return;
            }
            var changed = _device.Rotation != info.Rotation ||
                          _device.DisplayWidth != info.Width ||
                          _device.DisplayHeight != info.Height;
            _device = _device.WithDisplay(info.Rotation, info.Width, info.Height);
            var orientation = _device.Rotation switch
            {
                1 => "横屏 90°",
                2 => "倒置 180°",
                3 => "横屏 270°",
                _ => "竖屏"
            };
            TouchInfoText.Text =
                $"{_device.DisplayWidth}×{_device.DisplayHeight} · slot 0..{_device.SlotMax}";
            ToolTipService.SetToolTip(TouchInfoText,
                $"触摸屏 {_device.Name} · {orientation} · {_device.EventPath}");
            if (!changed)
            {
                return;
            }
            _pendingDisplay = true;
            await ApplyEnvironmentChangesAsync();
        }
        catch (Exception error)
        {
            LogPageError("RefreshDisplayAsync", error);
        }
        finally
        {
            if (version == _environmentVersion) _displayRefreshing = false;
        }
    }

    private void StartWatcher()
    {
        if (!_pageActive || !_session.IsConnected)
        {
            return;
        }
        if (_watcher is null)
        {
            _watcher = new ForegroundWatcher(_session.Client);
            _watcher.Changed += Watcher_Changed;
        }
        _watcher.Start();
        if (_watcher.Package is { Length: > 0 } package)
        {
            _ = HandleForegroundAsync(package);
        }
    }

    private void Watcher_Changed(object? sender, ForegroundChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_pageActive || !ReferenceEquals(sender, _watcher)) return;
            try
            {
                await HandleForegroundAsync(e.Package);
            }
            catch (Exception error)
            {
                LogPageError("Watcher_Changed", error);
                SetStatus(error.Message, true);
            }
        });
    }

    private async Task HandleForegroundAsync(string? package)
    {
        if (!_pageActive || string.IsNullOrWhiteSpace(package))
        {
            return;
        }

        ForegroundText.Text = "前台：" + package;
        _pendingForeground = package;
        await ApplyEnvironmentChangesAsync();
    }

    private async Task ApplyEnvironmentChangesAsync()
    {
        // All producers run on the UI dispatcher. Keep the latest app/display
        // request while a release ACK is pending, then drain it before exiting.
        if (_applyingEnvironment) return;
        _applyingEnvironment = true;
        var epoch = _pageEpoch;
        var version = _environmentVersion;
        try
        {
            while (_pageActive && epoch == _pageEpoch && version == _environmentVersion && (_pendingForeground is not null || _pendingDisplay))
            {
                await _engineTransitions.WaitAsync();
                try
                {
                    if (!_pageActive || epoch != _pageEpoch || version != _environmentVersion) return;
                    var package = _pendingForeground;
                    var displayChanged = _pendingDisplay;
                    _pendingForeground = null;
                    _pendingDisplay = false;
                    var profile = _profile;
                    if (package is not null)
                    {
                        var existing = _collection.Profiles.FirstOrDefault(p =>
                            string.Equals(p.Package, package, StringComparison.OrdinalIgnoreCase));
                        var candidate = existing ?? GamepadProfileStore.GetOrCreate(_collection, package);
                        if (existing is null) SaveCollection();
                        if (!_engine.IsRunning || candidate.AutoSwitch) profile = candidate;
                    }
                    var profileChanged = profile is not null && !ReferenceEquals(profile, _profile);
                    if (!displayChanged && !profileChanged) continue;
                    if (_engine.IsRunning && _device is not null && profile is not null)
                    {
                        ReleaseNativeMouseInput();
                        await _engine.ReconfigureAsync(_device, profile);
                        LogPageEvent($"configuration switched: {profile.Package}; connection retained");
                    }
                    if (!_pageActive || epoch != _pageEpoch || version != _environmentVersion) return;
                    if (profileChanged) SelectProfile(profile);
                    else DrawLayout();
                    SyncPlayModeUi(force: true);
                    UpdateMetrics();
                    if (_engine.IsRunning) SetStatus("操控中 · 已切换配置：" + profile?.DisplayName);
                }
                finally { _engineTransitions.Release(); }
            }
        }
        finally
        {
            _applyingEnvironment = false;
            if (_pageActive && (_pendingForeground is not null || _pendingDisplay)) _ = ApplyEnvironmentChangesAsync();
        }
    }

    private void Engine_Changed(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateMetrics);
    }

    private void UpdateMetrics()
    {
        if(_engine.IsRunning&&GyroToggle.IsEnabled&&GyroToggle.IsOn!=_engine.GyroMode)
        {
            var updating=_updatingUi;_updatingUi=true;GyroToggle.IsOn=_engine.GyroMode;_updatingUi=updating;
        }
        if (_controlOverlay is { } overlay)
        {
            if (_engine.IsRunning) overlay.Update(BuildOverlayState());
        }
        if (_controlActive && !_engine.IsRunning && !_startingControl && !_controlCleanupQueued)
        {
            _controlCleanupQueued = true;
            _ = CleanupStoppedControlAsync();
        }
        UpdateSnapshotUi();
        if (TraceToggle.IsChecked == true) ConfigureLiveTraces();
        var connected = _session.IsConnected;
        ConnectionDot.Fill = new SolidColorBrush(connected ? Colors.LightGreen : Colors.Gray);
        if (_pageActive && connected && !_developerPreviewAccess && (DateTime.UtcNow - _lastDisplayCheck).TotalSeconds >= 3)
        {
            _lastDisplayCheck = DateTime.UtcNow;
            _ = RefreshDisplayAsync();
        }
        var metrics = _engine.Metrics;
        RttText.Text = _engine.IsRunning
            ? $"触点 {metrics.ActiveSlots}/{metrics.SlotCapacity} · 等待 {metrics.PendingSlots} · {metrics.SamplingHz:F1} Hz · RTT {metrics.RttMs:F1} ms · {(_engine.FastConfigured ? "UDP + TCP" : "TCP")}"
            : "触点 — · RTT —";
        ToolTipService.SetToolTip(RttText,
            $"事件 {metrics.TotalEvents} · 发送 {metrics.EventsPerSecond:F1}/s\n" +
            $"采样 {metrics.SamplingHz:F1} / 240 Hz · P95 {metrics.SampleP95Ms:F2} ms · 错过周期 {metrics.MissedSamples}\n" +
            $"独立触摸连接 RTT {metrics.RttMs:F1} ms · {(metrics.LastError is { Length: > 0 } ? metrics.LastError : "运行正常")}");
        StartButton.Content = _developerPreviewAccess ? (_canvasPreview is null ? "离线运行" : "停止调试") : (_engine.IsRunning ? "停止操控" : "开始操控");
        if (_canvasPreview is not null)
        {
            UpdateCanvasPreviewStatus();
            return;
        }
        var looking = _engine.IsRunning && _engine.IsLooking;
        if (looking != _lastLooking)
        {
            _lastLooking = looking;
            if (_engine.IsRunning)
            {
                if (looking)
                {
                    UpdateCursor(_hasPointer ? _lastPointer : new Point(0, 0), false);
                }
                else if (_hasPointer)
                {
                    UpdateCursor(_lastPointer, true, forceShow: true);
                }
            }
            UpdateHintText();
        }
        SyncPlayModeUi();
        if (_engine.IsRunning && _engine.ViewMode && _mouseCapture.IsActive)
        {
            _mouseCapture.KeepHidden();
        }
        if (metrics.LastError is { Length: > 0 })
        {
            SetStatus(metrics.LastError, true);
        }
    }

    private ControlOverlayState BuildOverlayState()
    {
        var m = _engine.Metrics;
        return new(_device!.DisplayWidth, _device.DisplayHeight, _device.Rotation,
            m.ActiveSlots, Math.Max(1, _engine.IsRunning ? m.SlotCapacity : _device.SlotMax + 1), m.PendingSlots, m.SamplingHz, m.RttMs,
            _engine.IsMouseMode ? (byte)0 : _engine.ViewMode ? (byte)1 : (byte)2, IsAppActive());
    }

    private async Task CleanupStoppedControlAsync()
    {
        await _engineTransitions.WaitAsync();
        try
        {
            if (_controlActive && !_engine.IsRunning) await StopEngineCoreAsync();
        }
        finally { _controlCleanupQueued = false; _engineTransitions.Release(); }
    }

    private async Task StopOverlayControlAsync(ControlOverlaySession overlay, Exception? error = null)
    {
        await _engineTransitions.WaitAsync();
        try
        {
            if (!ReferenceEquals(_controlOverlay, overlay)) return;
            await StopEngineCoreAsync();
            if (error is not null) SetStatus("调试显示层连接中断，已停止操控：" + error.Message, true);
        }
        finally { _engineTransitions.Release(); }
    }

    private void SyncPlayModeUi(bool force = false)
    {
        var running = _engine.IsRunning;
        var viewMode = running && !_engine.IsMouseMode;
        if (_directTouchActive && (!running || viewMode))
            EndDirectTouch();
        if (!running)
        {
            _lastViewMode = null;
            ModeBadge.Visibility = Visibility.Collapsed;
            PlayModeBanner.Visibility = Visibility.Collapsed;
            _mouseCapture.Release();
            UpdateHintText();
            return;
        }

        ModeBadge.Visibility = Visibility.Visible;
        ModeBadgeText.Text = viewMode ? "视角模式" : "鼠标模式";
        ModeBadge.Background = new SolidColorBrush(viewMode
            ? Color.FromArgb(255, 0, 120, 220)
            : Color.FromArgb(255, 60, 64, 72));
        PlayModeBanner.Visibility = viewMode ? Visibility.Visible : Visibility.Collapsed;
        PlayModeBannerText.Text = "视角模式";
        var changed = _lastViewMode != viewMode;
        if (changed || force)
        {
            _lastViewMode = viewMode;
            DrawLayout();
            if (viewMode)
            {
                // Hide immediately even when the keyboard switched modes before
                // the pointer ever entered the canvas.
                UpdateCursor(_hasPointer ? _lastPointer : new Point(0, 0), false);
            }
            else if (_hasPointer)
            {
                // The engine may still hold the look touch for one frame after
                // the mode changes; show the cursor immediately anyway.
                UpdateCursor(_lastPointer, true, forceShow: true);
            }
            UpdateHintText();
            if (changed)
            {
                SetStatus(viewMode ? "视角模式" : "鼠标模式");
            }
        }
        if (changed || force || viewMode || !_mouseCapture.IsActive || _mouseCapture.IsHidden != viewMode)
        {
            UpdateMouseCapture(viewMode);
        }
    }

    private void UpdateHintText()
    {
        if (_canvasPreview is not null)
        {
            SubtitleText.Text = "本地预览";
            UpdateCanvasPreviewStatus();
            return;
        }
        if (_editorMode)
        {
            SubtitleText.Text = "编辑布局";
            HintText.Text = "";
            return;
        }
        if (_engine.IsRunning)
        {
            var modeKey = _profile?.Buttons.FirstOrDefault(b => b.Kind == GamepadControlKind.Look)?.Binding ?? "视角键";
            SubtitleText.Text = $"操控中 · {modeKey} 切换";
            HintText.Text = !_engine.IsMouseMode ? "视角模式" : "鼠标模式";
            return;
        }
        SubtitleText.Text = "";
        HintText.Text = "";
    }

    private void UpdateEditorAffordances()
    {
        var empty = _profile is null || _profile.Buttons.Count == 0;
        EmptyHint.Visibility = _editorMode && empty ? Visibility.Visible : Visibility.Collapsed;
        LayerFields.Visibility = _editorMode ? Visibility.Visible : Visibility.Collapsed;
        var hasButton = _selection is not null;
        DuplicateElementButton.IsEnabled = hasButton;
        DeleteElementButton.IsEnabled = _selection is not null;
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(error
            ? Colors.IndianRed
            : Color.FromArgb(255, 160, 160, 160));
    }

    private static void LogPageError(string where, Exception error) => BufferedLog.Write($"PAGE {where}: {error}");

    private static void LogPageEvent(string message) => BufferedLog.Write("PAGE " + message);

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_developerPreviewAccess) { await ToggleCanvasPreviewAsync(); UpdateMetrics(); return; }
            StopCanvasPreview();
            if (_editorMode)
            {
                SetStatus("请先点击「完成编辑」退出编辑模式。", true);
                return;
            }
            if (_engine.IsRunning)
            {
                await StopEngineAsync();
                return;
            }

            if (!_session.IsConnected)
            {
                SetStatus("请先在主页连接设备。", true);
                return;
            }
            if (_device is null)
            {
                await InitializeTouchAsync();
            }
            if (_device is null)
            {
                return;
            }
            await StartEngineAsync();
        }
        catch (Exception error)
        {
            LogPageError("StartButton_Click", error);
            SetStatus(error.Message, true);
        }
    }

    private async Task StartEngineAsync()
    {
        await _engineTransitions.WaitAsync();
        try
        {
            if (_pageActive && _pageReady) await StartEngineCoreAsync();
            else if (_pageActive) SetStatus("页面正在初始化，请稍后开始操控。");
        }
        finally { _engineTransitions.Release(); }
    }

    private async Task StartEngineCoreAsync()
    {
        if (_profile is null || _device is null)
        {
            return;
        }
        if (_engine.IsRunning) return;
        _startingControl = true;
        ControlOverlaySession? pendingOverlay = null;
        try
        {
            LogPageEvent("start requested");
            if (!NativeMouseHook.IsInstalled)
                throw new InvalidOperationException("原生鼠标捕获未就绪，请重新进入外设页面");
            var info = _session.Info;
            if (info is not null && !string.IsNullOrEmpty(_session.Options?.Host))
            {
                _engine.ConfigureFastPath(_session.Options.Host, info.FastPort, info.FastToken, info.FastProtocol, info.FastMaxRecords);
            }
            var epoch = _pageEpoch;
            var generation = _session.Client.ConnectionGeneration;
            if (BuildInfo.UseDebugOverlay(info))
                pendingOverlay = await ControlOverlaySession.StartAsync(_session.Client, BuildOverlayState());
            await _engine.StartAsync(_session.Client, _device, _profile);
            _controlActive = true;
            if(_profile.GyroEnabled)await _engine.SetGyroModeAsync(true);
            if (!_pageActive || epoch != _pageEpoch || generation != _session.Client.ConnectionGeneration)
            { await StopEngineCoreAsync(); return; }
            if (pendingOverlay is { } activeOverlay)
            {
                activeOverlay.Failed += error => DispatcherQueue.TryEnqueue(async () =>
                {
                    await StopOverlayControlAsync(activeOverlay, error);
                });
                _controlOverlay = activeOverlay; pendingOverlay = null;
                if (activeOverlay.Failure is { } overlayFailure) throw overlayFailure;
            }
            _engine.SetInputEnabled(IsAppActive());
            _engine.ClearInput();
            _hasPointer = false;
            ReleaseNativeMouseInput();
            _lastViewMode = null;
            _lastLooking = false;
            InputSurface.Focus(FocusState.Programmatic);
            await EnableCursorAsync();
            UpdateMetrics();
            SyncPlayModeUi(force: true);
            SetStatus("操控已开始");
        }
        catch (Exception error)
        {
            await StopEngineCoreAsync();
            LogPageError("StartEngineAsync", error);
            SetStatus(pendingOverlay is null && _controlOverlay is null && error.Message.Contains("ERROR ", StringComparison.Ordinal)
                ? "手机调试显示层启动失败，请更新模块至 0.12.8 或更新版本：" + error.Message : error.Message, true);
        }
        finally
        {
            if (pendingOverlay is not null) await pendingOverlay.DisposeAsync();
            _startingControl = false;
        }
    }

    private void ReleaseCursorChannel()
    {
        ++_cursorEpoch;
        _pendingCursor = null; _cursorUpdating = false;
        _cursorCts?.Cancel(); _cursorCts?.Dispose(); _cursorCts = null;
        _cursorClient?.Dispose(); _cursorClient = null;
    }

    private async Task EnableCursorAsync()
    {
        if (_profile is null || !_collection.CursorEnabled || _device is null) return;
        ReleaseCursorChannel();
        var epoch = _cursorEpoch;
        var pageEpoch = _pageEpoch;
        var generation = _session.Client.ConnectionGeneration;
        _cursorCts = new CancellationTokenSource();
        SkpdbClient? channel = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cursorCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            channel = await _session.Client.OpenIndependentChannelAsync(timeout.Token);
            try { await channel.CursorDestroyAsync(timeout.Token); }
            catch when (channel.IsConnected) { }
            await channel.CursorProbeAsync(timeout.Token);
            if (!_pageActive || pageEpoch != _pageEpoch || generation != _session.Client.ConnectionGeneration || epoch != _cursorEpoch)
            { await channel.CursorDestroyAsync(timeout.Token); return; }
            var x = Math.Max(0, _device.DisplayWidth / 2 - 32);
            var y = Math.Max(0, _device.DisplayHeight / 2 - 32);
            await channel.CursorSetAsync(x, y, true, timeout.Token);
            _cursorClient = channel; channel = null;
            _cursorActive = _cursorVisible = true;
            _lastCursorSent = DateTime.UtcNow;
        }
        catch (Exception error)
        {
            if (epoch == _cursorEpoch)
            {
                ReleaseCursorChannel(); _cursorActive = _cursorVisible = false;
                SetStatus("内核光标不可用: " + error.Message, true);
            }
        }
        finally { channel?.Dispose(); }
    }

    private async Task DisableCursorAsync()
    {
        var active = _cursorActive;
        ReleaseCursorChannel();
        _cursorActive = false;
        _cursorVisible = false;
        if (!active || !_session.IsConnected) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var channel = await _session.Client.OpenIndependentChannelAsync(timeout.Token);
            await channel.CursorSetAsync(0, 0, false, timeout.Token);
            await channel.CursorDestroyAsync(timeout.Token);
        }
        catch
        {
        }
    }

    private async Task StopEngineAsync()
    {
        await _engineTransitions.WaitAsync();
        try { await StopEngineCoreAsync(); }
        finally { _engineTransitions.Release(); }
    }

    private async Task StopEngineCoreAsync()
    {
        LogPageEvent("stop requested");
        _controlActive = false;
        ReleaseNativeMouseInput();
        await _engine.StopAsync();
        if (_controlOverlay is { } overlay) { _controlOverlay = null; await overlay.DisposeAsync(); }
        if (!_developerPreviewAccess) await DisableCursorAsync();
        _engine.ClearInput();
        _hasPointer = false;
        _lastViewMode = null;
        _lastLooking = false;
        SyncPlayModeUi(force: true);
        UpdateMetrics();
        SetStatus("操控已停止");
    }

    private void RefreshProfileList()
    {
        _updatingUi = true;
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _collection.Profiles;
        _updatingUi = false;
    }

    private void SelectProfile(GamepadProfile? profile, bool keepHistory = false)
    {
        CommitPropertyEdit();
        StopCanvasPreview();
        if (profile is null)
        {
            return;
        }
        if (!keepHistory && !ReferenceEquals(_profile, profile))
        {
            _undo.Clear();
            _redo.Clear();
            UpdateUndoButtons();
        }
        _profile = profile;
        _moveIndex = _lookIndex = 0;
        _selection = null;
        _selectedButtonId = null;
        ProfileTitle.Text = profile.DisplayName;
        ProfilePackage.Text = profile.Package;
        _updatingUi = true;
        PackageBox.Text = profile.Package;
        ProfileNameBox.Text = profile.DisplayName;
        CursorToggle.IsOn = _collection.CursorEnabled;
        GyroToggle.IsOn = _profile.GyroEnabled;
        ProfileList.SelectedItem = profile;
        _updatingUi = false;
        PopulateProperties(null);
        UpdateEditorAffordances();
        DrawLayout();
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || ProfileList.SelectedItem is not GamepadProfile profile)
        {
            return;
        }
        SelectProfile(profile);
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var package = PackageBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(package) || package == GamepadProfileStore.DefaultPackage)
        {
            package = _watcher?.Package ?? "com.example.game";
        }
        if (_collection.Profiles.Any(p => string.Equals(p.Package, package,
                StringComparison.OrdinalIgnoreCase)))
        {
            var suffix = 2;
            var basePackage = package;
            while (_collection.Profiles.Any(p => string.Equals(p.Package, package,
                       StringComparison.OrdinalIgnoreCase)))
            {
                package = basePackage + suffix++;
            }
        }
        var profile = GamepadProfileStore.CreateDefaultProfile(package, package);
        _collection.Profiles.Add(profile);
        SaveCollection();
        RefreshProfileList();
        SelectProfile(profile);
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
        {
            return;
        }
        var copy = _profile.Clone();
        var package = copy.Package + ".copy";
        var suffix = 2;
        while (_collection.Profiles.Any(p => string.Equals(p.Package, package,
                   StringComparison.OrdinalIgnoreCase)))
        {
            package = copy.Package + ".copy" + suffix++;
        }
        copy.Package = package;
        copy.DisplayName += " 副本";
        _collection.Profiles.Add(copy);
        SaveCollection();
        RefreshProfileList();
        SelectProfile(copy);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null || _profile.Package == GamepadProfileStore.DefaultPackage)
        {
            SetStatus("全局默认配置不能删除", true);
            return;
        }
        GamepadProfileStore.Delete(_collection, _profile.Package);
        SaveCollection();
        RefreshProfileList();
        SelectProfile(GamepadProfileStore.Find(_collection, GamepadProfileStore.DefaultPackage));
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
        {
            return;
        }
        var oldPackage = _profile.Package;
        var newPackage = PackageBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(newPackage) && newPackage != oldPackage)
        {
            if (_collection.Profiles.Any(p => p != _profile &&
                    string.Equals(p.Package, newPackage, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus("包名已存在", true);
                return;
            }
            _profile.Package = newPackage;
        }
        _profile.DisplayName = string.IsNullOrWhiteSpace(ProfileNameBox.Text)
            ? _profile.Package
            : ProfileNameBox.Text.Trim();
        try
        {
            _profile.Validate();
            SaveCollection();
            RefreshProfileList();
            SelectProfile(_profile, keepHistory: true);
            SetStatus("布局已保存");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    // ---------------------------------------------------------------- editing

    private void PushUndo()
    {
        CommitPropertyEdit();
        if (_profile is null)
        {
            return;
        }
        try
        {
            AddUndoSnapshot(GamepadProfileStore.SerializeProfile(_profile));
        }
        catch (Exception error)
        {
            LogPageError("PushUndo", error);
        }
    }

    private void AddUndoSnapshot(string snapshot)
    {
        _undo.Add(snapshot);
        while (_undo.Count > 80) _undo.RemoveAt(0);
        _redo.Clear(); UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _editorMode && _undo.Count > 0;
        RedoButton.IsEnabled = _editorMode && _redo.Count > 0;
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void RedoButton_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CommitPropertyEdit();
        if (_profile is null || _undo.Count == 0)
        {
            return;
        }
        try
        {
            _redo.Add(GamepadProfileStore.SerializeProfile(_profile));
            var json = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            RestoreProfile(json);
            SetStatus("已撤销");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
        UpdateUndoButtons();
    }

    private void Redo()
    {
        CommitPropertyEdit();
        if (_profile is null || _redo.Count == 0)
        {
            return;
        }
        try
        {
            _undo.Add(GamepadProfileStore.SerializeProfile(_profile));
            var json = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            RestoreProfile(json);
            SetStatus("已重做");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
        UpdateUndoButtons();
    }

    private void RestoreProfile(string json)
    {
        if (_profile is null)
        {
            return;
        }
        var restored = GamepadProfileStore.DeserializeProfile(json);
        if (restored is null)
        {
            return;
        }
        restored.Validate();
        var index = _collection.Profiles.FindIndex(p =>
            string.Equals(p.Package, _profile.Package, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _collection.Profiles[index] = restored;
        }
        else
        {
            _collection.Profiles.Add(restored);
        }
        _profile = restored;
        SaveCollection();
        RefreshProfileList();
        SelectProfile(restored, keepHistory: true);
        UpdateEditorAffordances();
        UpdateUndoButtons();
    }

    private void ApplySelected_Click(object sender, RoutedEventArgs e)
        => ApplySelectedProperties(save: true, refresh: true, recordUndo: true);

    private void ApplySelectedProperties(bool save, bool refresh, bool recordUndo)
    {
        if (_profile is null || _selection is null)
        {
            return;
        }

        try
        {
            if (recordUndo) CommitPropertyEdit();
            var before = recordUndo ? GamepadProfileStore.SerializeProfile(_profile) : null;
            if (_selection == "move")
            {
                SelectedMove.CenterX = ValueOr(XBox.Value, SelectedMove.CenterX);
                SelectedMove.CenterY = ValueOr(YBox.Value, SelectedMove.CenterY);
                SelectedMove.Radius = ValueOr(RadiusBox.Value, SelectedMove.Radius);
                SelectedMove.Deadzone = ValueOr(DeadzoneBox.Value, SelectedMove.Deadzone);
                SelectedMove.Snap8 = Snap8Toggle.IsOn;
                _profile.Move.Slot = (int)ValueOr(SlotBox.Value, _profile.Move.Slot);
                ReadMoveMotionFields(SelectedMove);
            }
            else if (_selection == "look")
            {
                SelectedLook.AnchorX = ValueOr(XBox.Value, SelectedLook.AnchorX);
                SelectedLook.AnchorY = ValueOr(YBox.Value, SelectedLook.AnchorY);
                SelectedLook.MaxRadiusX = ValueOr(LookWidthBox.Value, SelectedLook.MaxRadiusX * LogicalWidth * 2) / (LogicalWidth * 2);
                SelectedLook.MaxRadiusY = ValueOr(LookRadiusYBox.Value, SelectedLook.MaxRadiusY * LogicalHeight * 2) / (LogicalHeight * 2);
                SelectedLook.Sensitivity = ValueOr(SensitivityBox.Value, SelectedLook.Sensitivity);
                SelectedLook.Smoothing = ValueOr(SmoothingBox.Value, SelectedLook.Smoothing);
                SelectedLook.InvertY = InvertYToggle.IsOn;
                _profile.Look.Slot = (int)ValueOr(SlotBox.Value, _profile.Look.Slot);
                SelectedLook.RecenterMode = SelectedRecenterMode();
                SelectedLook.RecenterDelayMs =
                    (int)ValueOr(RecenterDelayBox.Value, SelectedLook.RecenterDelayMs);
                SelectedLook.RelayOverlapMs = (int)ValueOr(RelayOverlapBox.Value, SelectedLook.RelayOverlapMs);
                SelectedLook.RelayStartJitter = ValueOr(RelayStartJitterBox.Value, SelectedLook.RelayStartJitter);
                SelectedLook.RelayEarlyEnd = ValueOr(RelayEarlyEndBox.Value, SelectedLook.RelayEarlyEnd);
            }
            else if (_selectedButtonId is { Length: > 0 } id)
            {
                var button = _profile.Buttons.FirstOrDefault(b => b.Id == id);
                if (button is null)
                {
                    return;
                }
                button.X = ValueOr(XBox.Value, button.X);
                button.Y = ValueOr(YBox.Value, button.Y);
                button.Radius = ValueOr(RadiusBox.Value, button.Radius);
                button.Slot = (int)ValueOr(SlotBox.Value, button.Slot);
                var info = GamepadControlCatalog.Info(button.Kind);
                button.Label = string.IsNullOrWhiteSpace(LabelBox.Text)
                    ? info.Name
                    : LabelBox.Text.Trim();
                button.Binding = BindingBox.Text.Trim();
                if (info.UsesButtonMode)
                {
                    button.Mode = SelectedButtonMode();
                }
                else if (button.Kind == GamepadControlKind.Rapid)
                {
                    button.Mode = GamepadButtonMode.Rapid;
                }
                button.TapIntervalMs = (int)ValueOr(TapIntervalBox.Value, button.TapIntervalMs);
                button.DirectionX = ValueOr(DirectionXBox.Value, button.DirectionX);
                button.DirectionY = ValueOr(DirectionYBox.Value, button.DirectionY);
                button.Distance = ValueOr(DistanceBox.Value, button.Distance);
                button.DurationMs = (int)ValueOr(DurationBox.Value, button.DurationMs);
                button.Amount = ValueOr(AmountBox.Value, button.Amount);
            }
            _profile.Validate();
            if (before is not null && before != GamepadProfileStore.SerializeProfile(_profile)) AddUndoSnapshot(before);
            if (save) SaveCollection();
            DrawLayout();
            if (refresh) SelectElement(_selection, _selectedButtonId);
            SetStatus(save ? "元素属性已应用" : "画布已实时更新，修改自动保存");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    private static double ValueOr(double value, double fallback) =>
        double.IsNaN(value) ? fallback : value;

    private string SelectedRecenterMode()
    {
        if (RecenterBox.SelectedItem is ComboBoxItem { Tag: string mode })
        {
            return mode;
        }
        return "onIdle";
    }

    private void SelectRecenterMode(string mode)
    {
        for (var index = 0; index < RecenterBox.Items.Count; index++)
        {
            if (RecenterBox.Items[index] is ComboBoxItem { Tag: string tag } &&
                string.Equals(tag, mode, StringComparison.OrdinalIgnoreCase))
            {
                RecenterBox.SelectedIndex = index;
                return;
            }
        }
        RecenterBox.SelectedIndex = 0;
    }

    private void LayerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _profile is null)
        {
            return;
        }
        try
        {
            PushUndo();
            foreach (var m in _profile.Moves) m.Enabled = MoveVisibleToggle.IsOn;
            foreach (var l in _profile.Looks) l.Enabled = LookVisibleToggle.IsOn;
            _profile.Validate();
            SaveCollection();
            DrawLayout();
            UpdateEditorAffordances();
            SetStatus(SelectedMove.Enabled || SelectedLook.Enabled
                ? "图层已更新"
                : "轮盘与视角已隐藏");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    private async void ApplyCursor_Click(object sender, RoutedEventArgs e)
    {
        await ApplyCursorSettingsAsync();
    }

    private async void GyroToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }
        var requested=GyroToggle.IsOn;
        var targetProfile=_profile;
        if(targetProfile is null)return;
        GyroToggle.IsEnabled=false;
        try
        {
            if(!_engine.IsRunning){targetProfile.GyroEnabled=requested;SaveCollection();return;}
            await _engine.SetGyroModeAsync(requested);
            targetProfile.GyroEnabled=requested;SaveCollection();
            SetStatus(requested?"陀螺仪已确认启用；Direct Channel 尚未覆盖":"已回到触摸视角");
        }
        catch(Exception error)
        {
            _updatingUi=true;GyroToggle.IsOn=_engine.GyroMode;_updatingUi=false;
            SetStatus(error.Message,true);
        }
        finally{GyroToggle.IsEnabled=true;}
    }

    private void CursorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _profile is null)
        {
            return;
        }
        // Toggled can fire before IsOn reflects the new value. Apply after the
        // control finishes its own state update, otherwise turning the switch
        // back on can be seen as a second "off" and get reverted.
        DispatcherQueue.TryEnqueue(() => _ = ApplyCursorSettingsAsync());
    }

    private async Task ApplyCursorSettingsAsync()
    {
        if (_profile is null)
        {
            return;
        }
        _collection.CursorEnabled = CursorToggle.IsOn;
        SaveCollection();
        if (_developerPreviewAccess) { SetStatus("已保存光标设置；离线调试不操作设备"); return; }
        if (_collection.CursorEnabled)
        {
            if (_engine.IsRunning)
            {
                await EnableCursorAsync();
            }
        }
        else
        {
            // Turning the switch off must always clean up a process left from a
            // previous run, even when the gamepad engine is currently stopped.
            await DisableCursorAsync();
        }
        SetStatus(_collection.CursorEnabled ? "CPU 光标已启用" : "CPU 光标已关闭");
    }

    private void BuildKindList()
    {
        KindBox.Items.Clear();
        foreach (var info in GamepadControlCatalog.All)
        {
            KindBox.Items.Add(new ComboBoxItem { Content = info.Name, Tag = info.Kind });
        }
    }

    private void BuildModeList()
    {
        ButtonModeBox.Items.Clear();
        ButtonModeBox.Items.Add(new ComboBoxItem { Content = "按住（松开停止）", Tag = GamepadButtonMode.Hold });
        ButtonModeBox.Items.Add(new ComboBoxItem { Content = "切换（按一次开/关）", Tag = GamepadButtonMode.Toggle });
        ButtonModeBox.Items.Add(new ComboBoxItem { Content = "连发（按住连续点击）", Tag = GamepadButtonMode.Rapid });
    }

    private static int ModeIndex(GamepadButtonMode mode) =>
        mode switch { GamepadButtonMode.Toggle => 1, GamepadButtonMode.Rapid => 2, _ => 0 };

    private GamepadButtonMode SelectedButtonMode() =>
        ButtonModeBox.SelectedItem is ComboBoxItem { Tag: GamepadButtonMode mode }
            ? mode
            : GamepadButtonMode.Hold;

    private int FindFreeSlot(GamepadProfile profile)
    {
        var used = new HashSet<int> { profile.Move.Slot, profile.Look.Slot };
        foreach (var button in profile.Buttons)
        {
            used.Add(button.Slot);
        }
        var max = GamepadProfile.MaxLogicalSlot;
        for (var slot = 2; slot <= max; slot++)
        {
            if (!used.Contains(slot))
            {
                return slot;
            }
        }
        return -1;
    }

    private string NextButtonId(GamepadProfile profile, string prefix)
    {
        var index = 1;
        while (profile.Buttons.Any(button =>
                   string.Equals(button.Id, prefix + index, StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }
        return prefix + index;
    }

    private void AddControlAt(GamepadControlKind kind, Point normalized)
    {
        if (_profile is null)
        {
            return;
        }
        var slot = FindFreeSlot(_profile);
        if (slot < 0)
        {
            SetStatus("控件逻辑编号已用完", true);
            return;
        }
        var info = GamepadControlCatalog.Info(kind);
        try
        {
            PushUndo();
            var created = new GamepadButton
            {
                Id = NextButtonId(_profile, "control"),
                Label = info.Name,
                Kind = kind,
                X = Math.Round(Math.Clamp(normalized.X, 0.03, 0.97), 3),
                Y = Math.Round(Math.Clamp(normalized.Y, 0.03, 0.97), 3),
                Radius = info.Radius,
                Slot = slot,
                Binding = info.Binding,
                Mode = info.Mode,
                TapIntervalMs = 80,
                DirectionX = 0,
                DirectionY = -1,
                Distance = 0.16,
                DurationMs = 120,
                Amount = 0.14
            };
            _profile.Buttons.Add(created);
            _profile.Validate();
            SaveCollection();
            SelectElement("button", created.Id);
            SetStatus("已添加：" + created.Label + " · 编号 " + created.Slot);
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    private void AddMove_Click(object sender, RoutedEventArgs e) => AddAlternative(true);
    private void AddLook_Click(object sender, RoutedEventArgs e) => AddAlternative(false);
    private void AddAlternative(bool move, GamepadMoveSettings? source = null)
    {
        if (_profile is null || !_editorMode) return;
        PushUndo();
        if (move)
        {
            var copy = (source ?? SelectedMove).Clone(); copy.Enabled = true;
            copy.CenterX = Math.Clamp(copy.CenterX + .06, .05, .95); copy.CenterY = Math.Clamp(copy.CenterY - .06, .05, .95);
            _profile.ExtraMoves.Add(copy); _moveIndex = _profile.ExtraMoves.Count;
            SelectElement("move", _moveIndex.ToString());
        }
        else
        {
            var copy = SelectedLook.Clone(); copy.Enabled = true;
            copy.AnchorX = Math.Clamp(copy.AnchorX - .08, .05, .95); copy.AnchorY = Math.Clamp(copy.AnchorY + .06, .05, .95);
            _profile.ExtraLooks.Add(copy); _lookIndex = _profile.ExtraLooks.Count;
            SelectElement("look", _lookIndex.ToString());
        }
        _profile.Validate(); SaveCollection(); DrawLayout();
        SetStatus("已添加");
    }
    private void AddSprintHandle(double x, double y)
    {
        var handle = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Colors.Orange), Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1 };
        Canvas.SetLeft(handle, x - 6); Canvas.SetTop(handle, y - 6); LayoutCanvas.Children.Add(handle);
        AddLabel("Shift", x + 8, y - 9);
    }

    private void AddControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
        {
            return;
        }
        if (!_editorMode)
        {
            SetStatus("请先点击「编辑布局」", true);
            return;
        }
        var center = _screenRect.Width > 0 && _screenRect.Height > 0
            ? new Point(_screenRect.X + _screenRect.Width / 2, _screenRect.Y + _screenRect.Height / 2)
            : new Point(LayoutCanvas.ActualWidth / 2, LayoutCanvas.ActualHeight / 2);
        ShowAddKeyFlyout(center, ToNormalized(center));
    }

    private void ShowAddKeyFlyout(Point position, Point normalized)
    {
        if (_profile is null)
        {
            return;
        }
        var panel = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(12),
            Width = 372
        };
        panel.Children.Add(new TextBlock
        {
            Text = "添加按键",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 152, 158)),
            TextWrapping = TextWrapping.Wrap
        });

        var grid = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6
        };
        for (var column = 0; column < 3; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var rows = (GamepadControlCatalog.All.Count + 2) / 3;
        for (var row = 0; row < rows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Flyout? flyout = null;
        var index = 0;
        foreach (var info in GamepadControlCatalog.All)
        {
            var button = new Button
            {
                MinWidth = 0,
                MinHeight = 76,
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = info.Kind
            };
            var content = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            content.Children.Add(new FontIcon
            {
                Glyph = info.Glyph,
                FontSize = 24,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = info.Name,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            button.Content = content;
            ToolTipService.SetToolTip(button, info.Hint);
            var kind = info.Kind;
            var target = normalized;
            button.Click += (_, _) =>
            {
                flyout?.Hide();
                AddControlAt(kind, target);
            };
            Grid.SetColumn(button, index % 3);
            Grid.SetRow(button, index / 3);
            grid.Children.Add(button);
            index++;
        }
        var componentActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addMove = new Button { Content = "添加轮盘" };
        addMove.Click += (_, _) => { flyout?.Hide(); AddAlternative(true); SelectedMove.CenterX = normalized.X; SelectedMove.CenterY = normalized.Y; _profile.Validate(); SaveCollection(); DrawLayout(); FillMoveFields(); };
        var addLook = new Button { Content = "添加视角" };
        addLook.Click += (_, _) => { flyout?.Hide(); AddAlternative(false); SelectedLook.AnchorX = normalized.X; SelectedLook.AnchorY = normalized.Y; _profile.Validate(); SaveCollection(); DrawLayout(); FillLookFields(); };
        componentActions.Children.Add(addMove); componentActions.Children.Add(addLook); panel.Children.Add(componentActions);
        panel.Children.Add(new ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 330
        });
        flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.Bottom
        };
        flyout.ShowAt(LayoutCanvas, new FlyoutShowOptions
        {
            Position = position,
            Placement = position.Y > LayoutCanvas.ActualHeight * 0.5
                ? FlyoutPlacementMode.Top
                : FlyoutPlacementMode.Bottom
        });
    }

    private void ShowElementFlyout(Point position, string? kind, string? buttonId)
    {
        if (_profile is null)
        {
            return;
        }
        var menu = new MenuFlyout();
        var add = new MenuFlyoutItem { Text = "在此添加按键…" };
        add.Click += (_, _) => DispatcherQueue.TryEnqueue(() =>
            ShowAddKeyFlyout(position, ToNormalized(position)));
        menu.Items.Add(add);
        if (buttonId is { Length: > 0 })
        {
            var duplicate = new MenuFlyoutItem { Text = "复制此组件" };
            duplicate.Click += (_, _) => DuplicateSelected();
            menu.Items.Add(duplicate);
        }
        var delete = new MenuFlyoutItem { Text = "删除此组件" };
        delete.Click += (_, _) => DeleteSelected();
        menu.Items.Add(delete);
        menu.ShowAt(LayoutCanvas, new FlyoutShowOptions { Position = position });
    }

    private void DuplicateElement_Click(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void DuplicateSelected()
    {
        if (_profile is not null && _selection == "move") { AddAlternative(true, SelectedMove); return; }
        if (_profile is not null && _selection == "look") { AddAlternative(false); return; }
        if (_profile is null || _selectedButtonId is not { Length: > 0 } id)
        {
            SetStatus("请先选中一个组件", true);
            return;
        }
        var source = _profile.Buttons.FirstOrDefault(b => b.Id == id);
        if (source is null)
        {
            return;
        }
        var slot = FindFreeSlot(_profile);
        if (slot < 0)
        {
            SetStatus("控件逻辑编号已用完", true);
            return;
        }
        try
        {
            PushUndo();
            var copy = new GamepadButton
            {
                Id = NextButtonId(_profile, "control"),
                Label = source.Label,
                Kind = source.Kind,
                X = Math.Clamp(source.X + 0.04, 0.02, 0.98),
                Y = Math.Clamp(source.Y + 0.04, 0.02, 0.98),
                Radius = source.Radius,
                Slot = slot,
                Binding = source.Binding,
                Mode = source.Mode,
                TapIntervalMs = source.TapIntervalMs,
                DirectionX = source.DirectionX,
                DirectionY = source.DirectionY,
                Distance = source.Distance,
                DurationMs = source.DurationMs,
                Amount = source.Amount
            };
            _profile.Buttons.Add(copy);
            _profile.Validate();
            SaveCollection();
            SelectElement("button", copy.Id);
            SetStatus("已复制：" + copy.Label);
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    private void DeleteElement_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_profile is null || _selection is null)
        {
            return;
        }
        try
        {
            PushUndo();
            if (_selection == "move")
            {
                if (_moveIndex > 0) _profile.ExtraMoves.RemoveAt(_moveIndex - 1);
                else if (_profile.ExtraMoves.Count > 0) { _profile.Move = _profile.ExtraMoves[0]; _profile.ExtraMoves.RemoveAt(0); }
                else _profile.Move.Enabled = false;
                _moveIndex = 0;
            }
            else if (_selection == "look")
            {
                if (_lookIndex > 0) _profile.ExtraLooks.RemoveAt(_lookIndex - 1);
                else if (_profile.ExtraLooks.Count > 0) { _profile.Look = _profile.ExtraLooks[0]; _profile.ExtraLooks.RemoveAt(0); }
                else _profile.Look.Enabled = false;
                _lookIndex = 0;
            }
            else if (_selectedButtonId is { Length: > 0 } id)
            {
                _profile.Buttons.RemoveAll(b => b.Id == id);
            }
            _selection = null;
            _selectedButtonId = null;
            _profile.Validate();
            SaveCollection();
            PopulateProperties(null);
            UpdateEditorAffordances();
            DrawLayout();
            SetStatus("已删除");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null)
        {
            return;
        }
        try
        {
            PushUndo();
            var package = _profile.Package;
            var name = _profile.DisplayName;
            var fresh = GamepadProfileStore.CreateDefaultProfile(package, name);
            _profile.Move = fresh.Move;
            _profile.Look = fresh.Look;
            _profile.ExtraMoves.Clear(); _profile.ExtraLooks.Clear(); _moveIndex = _lookIndex = 0;
            _profile.Buttons = fresh.Buttons;
            _profile.Validate();
            SaveCollection();
            SelectElement(null, null);
            UpdateEditorAffordances();
            SetStatus("已恢复默认布局");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    private void PopulateProperties(string? kind, string? buttonId = null)
    {
        CommitPropertyEdit();
        _selection = kind;
        _selectedButtonId = buttonId;
        if (kind == "move" && int.TryParse(buttonId, out var moveIndex)) _moveIndex = moveIndex;
        if (kind == "look" && int.TryParse(buttonId, out var lookIndex)) _lookIndex = lookIndex;
        _updatingUi = true;
        try
        {
            MoveFields.Visibility = kind == "move" ? Visibility.Visible : Visibility.Collapsed;
            LookFields.Visibility = kind == "look" ? Visibility.Visible : Visibility.Collapsed;
            ButtonFields.Visibility = kind == "button" ? Visibility.Visible : Visibility.Collapsed;
            RadiusBox.Visibility = kind == "look" ? Visibility.Collapsed : Visibility.Visible;
            BasicFields.Visibility = kind is null ? Visibility.Collapsed : Visibility.Visible;
            ApplyButton.Visibility = kind is null ? Visibility.Collapsed : Visibility.Visible;
            InspectorHint.Visibility = kind is null ? Visibility.Visible : Visibility.Collapsed;
            MoveVisibleToggle.IsOn = _profile?.Move.Enabled ?? false;
            LookVisibleToggle.IsOn = _profile?.Look.Enabled ?? false;

            if (_profile is null || kind is null)
            {
                SelectedElementText.Text = "未选中";
                ClearFields();
                return;
            }

            if (kind == "move")
            {
                SelectedElementText.Text = "移动轮盘 · 编号 " + SelectedMove.Slot;
                FillMoveFields();
            }
            else if (kind == "look")
            {
                SelectedElementText.Text = "视角区域 · 编号 " + SelectedLook.Slot;
                FillLookFields();
            }
            else
            {
                var button = _profile.Buttons.FirstOrDefault(b => b.Id == buttonId);
                if (button is null)
                {
                    _selection = null;
                    _selectedButtonId = null;
                    SelectedElementText.Text = "未选中";
                    ClearFields();
                    return;
                }
                var info = GamepadControlCatalog.Info(button.Kind);
                SelectedElementText.Text = info.Name + " · 编号 " + button.Slot;
                FillButtonFields(button, info);
            }
            UpdateEditorAffordances();
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void ClearFields()
    {
        using var update = new EditorUiUpdate(this);
        XBox.Value = double.NaN;
        YBox.Value = double.NaN;
        RadiusBox.Value = double.NaN;
        SlotBox.Value = double.NaN;
        LabelBox.Text = string.Empty;
        BindingBox.Text = string.Empty;
    }

    private void FillMoveFields()
    {
        using var update = new EditorUiUpdate(this);
        if (_profile is null)
        {
            return;
        }
        RadiusBox.Minimum = .02; RadiusBox.Maximum = .35;
        XBox.Value = SelectedMove.CenterX;
        YBox.Value = SelectedMove.CenterY;
        RadiusBox.Value = SelectedMove.Radius;
        SlotBox.Value = SelectedMove.Slot;
        DeadzoneBox.Value = SelectedMove.Deadzone;
        Snap8Toggle.IsOn = SelectedMove.Snap8;
        MoveResponseBox.Value = SelectedMove.ResponseMs;
        MoveStartBox.Value = SelectedMove.StartSpread;
        MoveTargetBox.Value = SelectedMove.TargetSpread;
        MoveCurveBox.Value = SelectedMove.Curvature;
        MoveDistortionBox.Value = SelectedMove.Distortion;
        MoveAngularBox.Value = SelectedMove.AngularSpread;
        SprintExtraBox.Value = SelectedMove.SprintExtra;
        MoveSwayBox.Value = SelectedMove.HoldSway;
    }

    private void ReadMoveMotionFields(GamepadMoveSettings move)
    {
        move.ResponseMs = (int)ValueOr(MoveResponseBox.Value, move.ResponseMs);
        move.StartSpread = ValueOr(MoveStartBox.Value, move.StartSpread);
        move.TargetSpread = ValueOr(MoveTargetBox.Value, move.TargetSpread);
        move.Curvature = ValueOr(MoveCurveBox.Value, move.Curvature);
        move.Distortion = ValueOr(MoveDistortionBox.Value, move.Distortion);
        move.AngularSpread = MoveAngularBox.Value;
        move.SprintExtra = SprintExtraBox.Value;
        move.HoldSway = MoveSwayBox.Value;
    }

    private void TraceToggle_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = TraceToggle.IsChecked == true;
        CanvasTraceView.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _engine.TouchTrace.Enabled = enabled;
        if (_canvasPreview is not null) _canvasPreview.Trace.Enabled = enabled;
        if (enabled)
        {
            _traceLayout = null; ConfigureLiveTraces(); _traceTimer?.Start();
        }
        else if (_canvasPreview is null) _traceTimer?.Stop();
        if (_profile is not null) DrawLayout();
    }

    private void ConfigureLiveTraces()
    {
        if (_profile is not { } profile) return;
        var device = _previewDevice ?? _device ?? new TouchDeviceInfo("preview", "", "", profile.ScreenWidth, profile.ScreenHeight, 9, true, true);
        var move = profile.Move; var look = profile.Look;
        var layout = $"{device.DisplayWidth}/{device.DisplayHeight}/{device.Rotation}/{device.SlotMax}/" +
            $"{move.Slot}/{move.Enabled}/{move.CenterX}/{move.CenterY}/{move.Radius}/{move.Deadzone}/" +
            $"{look.Slot}/{look.AnchorX}/{look.AnchorY}/{look.MaxRadiusX}/{look.MaxRadiusY}/" +
            string.Join(',', profile.Buttons.Select(b => b.Slot));
        if (_traceLayout == layout) return;
        _traceLayout = layout; _engine.TouchTrace.Drain();
        CanvasTraceView.ConfigureCanvas(device);
        CanvasTraceView.SetScreenRect(_screenRect);
    }

    private void FillLookFields()
    {
        using var update = new EditorUiUpdate(this);
        LookWidthBox.Minimum = Math.Max(20, Math.Ceiling(LogicalWidth * .02));
        LookWidthBox.Maximum = LogicalWidth;
        LookRadiusYBox.Minimum = Math.Max(20, Math.Ceiling(LogicalHeight * .02));
        LookRadiusYBox.Maximum = LogicalHeight;
        if (_profile is null)
        {
            return;
        }
        XBox.Value = SelectedLook.AnchorX;
        YBox.Value = SelectedLook.AnchorY;
        RadiusBox.Value = SelectedLook.MaxRadiusX;
        LookWidthBox.Value = Math.Round(SelectedLook.MaxRadiusX * LogicalWidth * 2);
        LookRadiusYBox.Value = Math.Round(SelectedLook.MaxRadiusY * LogicalHeight * 2);
        SlotBox.Value = SelectedLook.Slot;
        SensitivityBox.Value = SelectedLook.Sensitivity;
        SmoothingBox.Value = SelectedLook.Smoothing;
        InvertYToggle.IsOn = SelectedLook.InvertY;
        RecenterDelayBox.Value = SelectedLook.RecenterDelayMs;
        RelayOverlapBox.Value = SelectedLook.RelayOverlapMs;
        RelayStartJitterBox.Value = SelectedLook.RelayStartJitter;
        RelayEarlyEndBox.Value = SelectedLook.RelayEarlyEnd;
        SelectRecenterMode(SelectedLook.RecenterMode);
    }

    private void FillButtonFields(GamepadButton button, GamepadControlKindInfo info)
    {
        using var update = new EditorUiUpdate(this);
        RadiusBox.Minimum = .01; RadiusBox.Maximum = .3;
        XBox.Value = button.X;
        YBox.Value = button.Y;
        RadiusBox.Value = button.Radius;
        SlotBox.Value = button.Slot;
        LabelBox.Text = button.Label;
        BindingBox.Text = button.Binding;
        TapIntervalBox.Value = button.TapIntervalMs;
        DirectionXBox.Value = button.DirectionX;
        DirectionYBox.Value = button.DirectionY;
        DistanceBox.Value = button.Distance;
        DurationBox.Value = button.DurationMs;
        AmountBox.Value = button.Amount;

        var kindIndex = GamepadControlCatalog.All
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair => pair.item.Kind == button.Kind).index;
        KindBox.SelectedIndex = Math.Max(0, kindIndex);

        ButtonModeFields.Visibility = info.UsesButtonMode ? Visibility.Visible : Visibility.Collapsed;
        ButtonModeBox.SelectedIndex = ModeIndex(button.Mode);
        IntervalFields.Visibility = info.UsesInterval || info.UsesButtonMode ? Visibility.Visible : Visibility.Collapsed;
        GestureFields.Visibility = info.UsesDirection || info.UsesDuration || info.UsesAmount
            ? Visibility.Visible
            : Visibility.Collapsed;
        DirectionXBox.Visibility = info.UsesDirection ? Visibility.Visible : Visibility.Collapsed;
        DirectionYBox.Visibility = info.UsesDirection ? Visibility.Visible : Visibility.Collapsed;
        DistanceBox.Visibility = info.UsesDistance ? Visibility.Visible : Visibility.Collapsed;
        DurationBox.Visibility = info.UsesDuration ? Visibility.Visible : Visibility.Collapsed;
        AmountBox.Visibility = info.UsesAmount ? Visibility.Visible : Visibility.Collapsed;
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || _profile is null || _selection != "button" ||
            _selectedButtonId is not { Length: > 0 } id)
        {
            return;
        }
        if (KindBox.SelectedItem is not ComboBoxItem { Tag: GamepadControlKind kind })
        {
            return;
        }
        var button = _profile.Buttons.FirstOrDefault(b => b.Id == id);
        if (button is null || button.Kind == kind)
        {
            return;
        }
        try
        {
            var oldInfo = GamepadControlCatalog.Info(button.Kind);
            var newInfo = GamepadControlCatalog.Info(kind);
            PushUndo();
            button.Kind = kind;
            if (string.IsNullOrWhiteSpace(button.Label) ||
                string.Equals(button.Label, oldInfo.Name, StringComparison.Ordinal))
            {
                button.Label = newInfo.Name;
            }
            if (string.IsNullOrWhiteSpace(button.Binding) ||
                string.Equals(button.Binding, oldInfo.Binding, StringComparison.OrdinalIgnoreCase))
            {
                button.Binding = newInfo.Binding;
            }
            if (Math.Abs(button.Radius - oldInfo.Radius) < 0.0001)
            {
                button.Radius = newInfo.Radius;
            }
            button.Mode = newInfo.Mode;
            _profile.Validate();
            SaveCollection();
            SelectElement("button", id);
            SetStatus("按键类型已改为：" + newInfo.Name);
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
    }

    private void CaptureBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selection != "button" || _selectedButtonId is null)
        {
            SetStatus("请先选中一个按键", true);
            return;
        }
        if (_capturingBinding)
        {
            CancelBindingCapture();
            SetStatus("已取消按键捕获");
            return;
        }
        _capturingBinding = true;
        CaptureBindingButton.Content = "取消";
        CaptureHintText.Visibility = Visibility.Visible;
        InputSurface.Focus(FocusState.Programmatic);
        SetStatus("请按下要绑定的键盘键或鼠标键（Esc 取消）");
    }

    private void CancelBindingCapture()
    {
        _capturingBinding = false;
        CaptureBindingButton.Content = "捕获";
        CaptureHintText.Visibility = Visibility.Collapsed;
    }

    private void ApplyCapturedBinding(string name)
    {
        if (_profile is null || _selectedButtonId is not { Length: > 0 } id)
        {
            CancelBindingCapture();
            return;
        }
        var button = _profile.Buttons.FirstOrDefault(b => b.Id == id);
        if (button is null)
        {
            CancelBindingCapture();
            return;
        }
        try
        {
            PushUndo();
            button.Binding = name;
            BindingBox.Text = name;
            _profile.Validate();
            SaveCollection();
            DrawLayout();
            SetStatus("已绑定：" + name);
        }
        catch (Exception error)
        {
            SetStatus(error.Message, true);
        }
        CancelBindingCapture();
    }

    private void SelectElement(string? kind, string? buttonId)
    {
        PopulateProperties(kind, buttonId);
        DrawLayout();
        UpdateEditorAffordances();
    }

    private void LayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawLayout();

    private void InputSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A Canvas does not always take the arranged size of its Border. Pin it
        // to the surface so the phone preview uses every available pixel.
        if (e.NewSize.Width > 0 && Math.Abs(LayoutCanvas.Width - e.NewSize.Width) > 0.5)
        {
            LayoutCanvas.Width = e.NewSize.Width;
        }
        if (e.NewSize.Height > 0 && Math.Abs(LayoutCanvas.Height - e.NewSize.Height) > 0.5)
        {
            LayoutCanvas.Height = e.NewSize.Height;
        }
        DrawLayout();
        UpdateMouseCaptureClip();
    }

    private void DrawLayout()
    {
        RefreshElementPicker();
        if (_profile is null)
        {
            return;
        }
        var canvas = LayoutCanvas;
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width < 20 || height < 20)
        {
            return;
        }

        var screenW = Math.Max(1, LogicalWidth);
        var screenH = Math.Max(1, LogicalHeight);
        var scale = Math.Min((width - 24) / screenW, (height - 24) / screenH);
        if (scale <= 0)
        {
            return;
        }
        var rectW = screenW * scale;
        var rectH = screenH * scale;
        _screenRect = new Rect((width - rectW) / 2, (height - rectH) / 2, rectW, rectH);
        Canvas.SetLeft(PreviewWatermarkBounds, _screenRect.X);
        Canvas.SetTop(PreviewWatermarkBounds, _screenRect.Y);
        PreviewWatermarkBounds.Width = rectW;
        PreviewWatermarkBounds.Height = rectH;
        PreviewWatermarkClip.Rect = new Rect(0, 0, rectW, rectH);
        PreviewWatermark.FontSize = Math.Clamp(Math.Min(rectW / 7.5, rectH / 3.5), 14, 60);
        ConfigureLiveTraces();
        CanvasTraceView.SetScreenRect(_screenRect);

        canvas.Children.Clear();
        AddRect(_screenRect, new SolidColorBrush(Color.FromArgb(255, 22, 24, 28)),
            new SolidColorBrush(Color.FromArgb(255, 70, 74, 82)), 1.5);
        DrawSnapshotBackground();
        if (_editorMode)
        {
            for (var i = 1; i < 4; i++)
            {
                var x = _screenRect.X + _screenRect.Width * i / 4;
                AddLine(x, _screenRect.Y, x, _screenRect.Bottom,
                    Color.FromArgb(40, 100, 110, 125));
            }
            for (var i = 1; i < 8; i++)
            {
                var y = _screenRect.Y + _screenRect.Height * i / 8;
                AddLine(_screenRect.X, y, _screenRect.Right, y,
                    Color.FromArgb(40, 100, 110, 125));
            }
        }

        foreach (var (move, moveIndex) in _profile.Moves.Select((m, i) => (m, i)))
        {
        if ((_editorMode || _canvasPreview is not null || TraceToggle.IsChecked == true) && move.Enabled)
        {
            var cx = _screenRect.X + move.CenterX * _screenRect.Width;
            var cy = _screenRect.Y + move.CenterY * _screenRect.Height;
            var r = CanvasMoveRadius(move);
            var fill = (_selection == "move" && moveIndex == _moveIndex)
                ? Color.FromArgb(90, 0, 150, 255)
                : Color.FromArgb(55, 70, 160, 230);
            var ellipse = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = new SolidColorBrush(fill),
                Stroke = new SolidColorBrush((_selection == "move" && moveIndex == _moveIndex) ? Colors.DeepSkyBlue : Colors.SteelBlue),
                StrokeThickness = (_selection == "move" && moveIndex == _moveIndex) ? 2 : 1
            };
            Canvas.SetLeft(ellipse, cx - r);
            Canvas.SetTop(ellipse, cy - r);
            canvas.Children.Add(ellipse);
            var sprintRadius = r * (1 + move.SprintExtra);
            var sprintRing = new Ellipse { Width = sprintRadius * 2, Height = sprintRadius * 2,
                Stroke = new SolidColorBrush(Colors.Orange), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 } };
            Canvas.SetLeft(sprintRing, cx - sprintRadius); Canvas.SetTop(sprintRing, cy - sprintRadius); canvas.Children.Add(sprintRing);
            AddCenteredLabel("移动 " + (moveIndex + 1), cx, cy - 8);
            if (_editorMode && _selection == "move" && moveIndex == _moveIndex)
            { AddSprintHandle(cx + sprintRadius, cy); }
            if (_editorMode && (_selection == "move" && moveIndex == _moveIndex))
            {
                AddResizeHandle(cx + (r + 5) * 0.7071, cy + (r + 5) * 0.7071);
            }
        }

        }
        foreach (var (look, lookIndex) in _profile.Looks.Select((l, i) => (l, i)))
        {
        if ((_editorMode || _canvasPreview is not null || TraceToggle.IsChecked == true) && look.Enabled)
        {
            var left = _screenRect.X + (look.AnchorX - look.MaxRadiusX) * _screenRect.Width;
            var top = _screenRect.Y + (look.AnchorY - look.MaxRadiusY) * _screenRect.Height;
            var lookRect = new Rect(left, top,
                look.MaxRadiusX * 2 * _screenRect.Width,
                look.MaxRadiusY * 2 * _screenRect.Height);
            AddRect(lookRect,
                new SolidColorBrush((_selection == "look" && lookIndex == _lookIndex)
                    ? Color.FromArgb(70, 0, 150, 255)
                    : Color.FromArgb(35, 120, 160, 220)),
                new SolidColorBrush((_selection == "look" && lookIndex == _lookIndex) ? Colors.DeepSkyBlue : Colors.SlateGray),
                (_selection == "look" && lookIndex == _lookIndex) ? 2 : 1);
            AddLabel("视角 " + (lookIndex + 1), lookRect.X + 8, lookRect.Y + 8);
            if (_editorMode && _selection == "look" && lookIndex == _lookIndex) AddResizeHandle(lookRect.Right, lookRect.Bottom);
        }

        }
        foreach (var button in _profile.Buttons)
        {
            var cx = _screenRect.X + button.X * _screenRect.Width;
            var cy = _screenRect.Y + button.Y * _screenRect.Height;
            var r = Math.Max(9, button.Radius * Math.Min(_screenRect.Width, _screenRect.Height));
            var selected = _editorMode && _selectedButtonId == button.Id;
            var active = (button.Kind == GamepadControlKind.Look && _engine.ViewMode);
            var info = GamepadControlCatalog.Info(button.Kind);
            var fill = selected || active
                ? Color.FromArgb(165, 0, 120, 220)
                : Color.FromArgb(100, 45, 55, 70);
            var ellipse = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = new SolidColorBrush(fill),
                Stroke = new SolidColorBrush(selected || active
                    ? Colors.DeepSkyBlue
                    : Color.FromArgb(215, 150, 165, 185)),
                StrokeThickness = selected || active ? 2 : 1
            };
            Canvas.SetLeft(ellipse, cx - r);
            Canvas.SetTop(ellipse, cy - r);
            canvas.Children.Add(ellipse);
            AddGlyph(info.Glyph, cx, cy, Math.Max(12, r * 1.02), Colors.White);
            if (button.Kind == GamepadControlKind.DPad)
            {
                AddLine(cx - r * 0.45, cy, cx + r * 0.45, cy, Colors.DeepSkyBlue);
                AddLine(cx, cy - r * 0.45, cx, cy + r * 0.45, Colors.DeepSkyBlue);
            }
            else if (button.Kind == GamepadControlKind.Crosshair)
            {
                AddLine(cx - r * 0.75, cy, cx - r * 0.35, cy, Colors.DeepSkyBlue);
                AddLine(cx + r * 0.35, cy, cx + r * 0.75, cy, Colors.DeepSkyBlue);
                AddLine(cx, cy - r * 0.75, cx, cy - r * 0.35, Colors.DeepSkyBlue);
                AddLine(cx, cy + r * 0.35, cx, cy + r * 0.75, Colors.DeepSkyBlue);
            }
            if (_editorMode && selected)
            {
                var labelTop = cy + r + 2;
                if (labelTop > _screenRect.Bottom - 14)
                {
                    labelTop = cy - r - 17;
                }
                AddCenteredLabel(button.Label, cx, labelTop);
                AddCenteredLabel("编号 " + button.Slot, cx, cy - r - 17);
                AddResizeHandle(cx + (r + 5) * 0.7071, cy + (r + 5) * 0.7071);
            }
        }
        UpdateEditorAffordances();
    }

    private void AddResizeHandle(double x, double y)
    {
        var handle = new Rectangle
        {
            Width = 10,
            Height = 10,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(Colors.White),
            Stroke = new SolidColorBrush(Colors.DeepSkyBlue),
            StrokeThickness = 1.5
        };
        Canvas.SetLeft(handle, x - 5);
        Canvas.SetTop(handle, y - 5);
        LayoutCanvas.Children.Add(handle);
    }

    private void AddGlyph(string glyph, double centerX, double centerY, double size, Color color)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = size,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(icon, centerX - icon.DesiredSize.Width / 2);
        Canvas.SetTop(icon, centerY - icon.DesiredSize.Height / 2);
        LayoutCanvas.Children.Add(icon);
    }

    private void AddRect(Rect rect, Brush fill, Brush stroke, double thickness)
    {
        var shape = new Rectangle
        {
            Width = Math.Max(1, rect.Width),
            Height = Math.Max(1, rect.Height),
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness,
            RadiusX = 8,
            RadiusY = 8
        };
        Canvas.SetLeft(shape, rect.X);
        Canvas.SetTop(shape, rect.Y);
        LayoutCanvas.Children.Add(shape);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Color color)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1
        };
        LayoutCanvas.Children.Add(line);
    }

    private void AddLabel(string text, double left, double top)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Colors.White),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        LayoutCanvas.Children.Add(label);
    }

    private void AddCenteredLabel(string text, double centerX, double top)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Colors.White),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        LayoutCanvas.Children.Add(label);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = Math.Clamp(centerX - label.DesiredSize.Width / 2, 4,
            Math.Max(4, LayoutCanvas.ActualWidth - label.DesiredSize.Width - 4));
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
    }

    private void UpdateCursor(Point position, bool visible, bool forceShow = false)
    {
        if (!_cursorActive || _device is null || _profile is null)
        {
            return;
        }
        if (visible && !forceShow && _profile.Cursor.HideDuringLook &&
            _engine.IsLooking)
        {
            visible = false;
        }
        var normalized = ToNormalized(position);
        var x = (int)Math.Clamp(normalized.X * _device.DisplayWidth, 0,
            Math.Max(0, _device.DisplayWidth - 64));
        var y = (int)Math.Clamp(normalized.Y * _device.DisplayHeight, 0,
            Math.Max(0, _device.DisplayHeight - 64));

        if (visible != _cursorVisible)
        {
            _cursorVisible = visible;
            _lastCursorSent = DateTime.UtcNow;
            _ = SetCursorVisibleAsync(x, y, visible);
            return;
        }
        if (!visible)
        {
            return;
        }

        // Movement is connectionless and coalesced by the module/renderer.
        // Falling back to TCP at 8 ms keeps a missing fast path usable without
        // creating one request per mouse event.
        if (_engine.SendCursorMove(x, y))
        {
            return;
        }
        var now = DateTime.UtcNow;
        if ((now - _lastCursorSent).TotalMilliseconds < 8)
        {
            return;
        }
        _lastCursorSent = now;
        _ = SetCursorVisibleAsync(x, y, true);
    }

    private async Task SetCursorVisibleAsync(int x, int y, bool visible)
    {
        _pendingCursor = (x, y, visible);
        if (_cursorUpdating || _cursorClient is not { } client || _cursorCts is not { } lifetime) return;
        _cursorUpdating = true;
        var epoch = _cursorEpoch;
        try
        {
            while (epoch == _cursorEpoch && _pendingCursor is { } next)
            {
                _pendingCursor = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await client.CursorSetAsync(next.X, next.Y, next.Visible, timeout.Token);
            }
        }
        catch
        {
            if (epoch == _cursorEpoch)
            { ReleaseCursorChannel(); _cursorActive = _cursorVisible = false; }
        }
        finally { if (epoch == _cursorEpoch) _cursorUpdating = false; }
    }

    private void EndDirectTouch()
    {
        if (!_directTouchActive)
            return;
        _directTouchActive = false;
        _engine.PointerTouchUp();
    }

    private void LayoutCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (PreviewPointerPressed(e)) return;
        if (_profile is null || !_editorMode)
        {
            return;
        }
        var point = e.GetCurrentPoint(LayoutCanvas);
        if (DateTime.UtcNow <= _suppressCanvasPressUntil)
        {
            _suppressCanvasPressUntil = DateTime.MinValue;
            e.Handled = true;
            return;
        }
        if (_capturingBinding)
        {
            // The global mouse hook turns the next press into a binding. The
            // canvas must not start a drag or open the add menu at the same
            // time, otherwise binding a mouse button also edits the layout.
            e.Handled = true;
            return;
        }
        if (_editorMode)
        {
            if (point.Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                HandleEditorRightClick(point.Position);
                return;
            }
            if (!point.Properties.IsLeftButtonPressed)
            {
                return;
            }
            InputSurface.Focus(FocusState.Pointer);
            if (TryStartResize(point.Position))
            {
                LayoutCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            var hit = HitTest(ToNormalized(point.Position));
            if (hit.Kind is null)
            {
                SelectElement(null, null);
                e.Handled = true;
                return;
            }
            SelectElement(hit.Kind, hit.ButtonId);
            _dragKind = hit.Kind == "button" ? "button" : hit.Kind;
            _draggingLayout = true;
            _dragUndoPushed = false;
            _lastPointer = point.Position;
            LayoutCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

    }

    private void HandleEditorRightClick(Point position)
    {
        if (!_screenRect.Contains(position))
        {
            SetStatus("请在手机屏幕区域内右键添加按键", true);
            return;
        }
        var hit = HitTest(ToNormalized(position));
        if (hit.Kind is not null)
        {
            SelectElement(hit.Kind, hit.ButtonId);
            ShowElementFlyout(position, hit.Kind, hit.ButtonId);
            return;
        }
        ShowAddKeyFlyout(position, ToNormalized(position));
    }

    private bool TryStartResize(Point position)
    {
        if (_profile is null)
        {
            return false;
        }
        double cx;
        double cy;
        double r;
        string target;
        if (_selection == "look" && SelectedLook.Enabled)
        {
            cx = _screenRect.X + SelectedLook.AnchorX * _screenRect.Width;
            cy = _screenRect.Y + SelectedLook.AnchorY * _screenRect.Height;
            var hx = cx + SelectedLook.MaxRadiusX * _screenRect.Width;
            var hy = cy + SelectedLook.MaxRadiusY * _screenRect.Height;
            if (Math.Abs(position.X - hx) > 9 || Math.Abs(position.Y - hy) > 9) return false;
            _resizingLayout = true; _resizeTarget = "look"; _resizeCenter = new Point(cx, cy);
            _dragUndoPushed = false; return true;
        }
        if (_selection == "move" && SelectedMove.Enabled)
        {
            cx = _screenRect.X + SelectedMove.CenterX * _screenRect.Width;
            cy = _screenRect.Y + SelectedMove.CenterY * _screenRect.Height;
            r = CanvasMoveRadius(SelectedMove);
            var sprintX = cx + r * (1 + SelectedMove.SprintExtra);
            if (Math.Abs(position.X - sprintX) <= 9 && Math.Abs(position.Y - cy) <= 9)
            {
                _resizingLayout = true; _resizeTarget = "sprint"; _resizeCenter = new Point(cx, cy);
                _dragUndoPushed = false; return true;
            }
            target = "move";
        }
        else if (_selectedButtonId is { Length: > 0 } id)
        {
            var button = _profile.Buttons.FirstOrDefault(b => b.Id == id);
            if (button is null)
            {
                return false;
            }
            cx = _screenRect.X + button.X * _screenRect.Width;
            cy = _screenRect.Y + button.Y * _screenRect.Height;
            r = Math.Max(9, button.Radius * Math.Min(_screenRect.Width, _screenRect.Height));
            target = "button";
        }
        else
        {
            return false;
        }

        // The handle sits just outside the ring so pressing the middle of a
        // small key never starts a resize instead of a drag.
        var handleX = cx + (r + 5) * 0.7071;
        var handleY = cy + (r + 5) * 0.7071;
        if (Math.Abs(position.X - handleX) > 9 || Math.Abs(position.Y - handleY) > 9)
        {
            return false;
        }
        _resizingLayout = true;
        _resizeTarget = target;
        _resizeCenter = new Point(cx, cy);
        _dragUndoPushed = false;
        _lastPointer = position;
        return true;
    }

    private void LayoutCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (PreviewPointerMoved(e)) return;
        if (_profile is null || !_editorMode)
        {
            return;
        }
        var position = e.GetCurrentPoint(LayoutCanvas).Position;
        if (_resizingLayout)
        {
            ApplyResize(position);
            return;
        }
        if (!_draggingLayout)
        {
            return;
        }
        if (!_dragUndoPushed)
        {
            PushUndo();
            _dragUndoPushed = true;
        }
        var normalized = ToNormalized(position);
        if (_dragKind == "move")
        {
            SelectedMove.CenterX = Math.Round(Math.Clamp(normalized.X, 0, 1), 3);
            SelectedMove.CenterY = Math.Round(Math.Clamp(normalized.Y, 0, 1), 3);
            XBox.Value = SelectedMove.CenterX;
            YBox.Value = SelectedMove.CenterY;
        }
        else if (_dragKind == "look")
        {
            SelectedLook.AnchorX = Math.Round(Math.Clamp(normalized.X, 0, 1), 3);
            SelectedLook.AnchorY = Math.Round(Math.Clamp(normalized.Y, 0, 1), 3);
            XBox.Value = SelectedLook.AnchorX;
            YBox.Value = SelectedLook.AnchorY;
        }
        else if (_selectedButtonId is { Length: > 0 } id)
        {
            var button = _profile.Buttons.FirstOrDefault(b => b.Id == id);
            if (button is not null)
            {
                button.X = Math.Round(Math.Clamp(normalized.X, 0, 1), 3);
                button.Y = Math.Round(Math.Clamp(normalized.Y, 0, 1), 3);
                XBox.Value = button.X;
                YBox.Value = button.Y;
            }
        }
        _lastPointer = position;
        DrawLayout();
    }

    private void ApplyResize(Point position)
    {
        if (_profile is null)
        {
            return;
        }
        var dx = position.X - _resizeCenter.X;
        var dy = position.Y - _resizeCenter.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var normalized = distance / Math.Max(1, Math.Min(_screenRect.Width, _screenRect.Height));
        if (!_dragUndoPushed)
        {
            PushUndo();
            _dragUndoPushed = true;
        }
        if (_resizeTarget == "sprint")
        {
            SelectedMove.SprintExtra = Math.Round(Math.Clamp(distance / Math.Max(1, CanvasMoveRadius(SelectedMove)) - 1, 0, 1), 3);
            SprintExtraBox.Value = SelectedMove.SprintExtra;
        }
        else if (_resizeTarget == "look")
        {
            SelectedLook.MaxRadiusX = Math.Clamp(Math.Abs(dx) / _screenRect.Width, .01, .5);
            SelectedLook.MaxRadiusY = Math.Clamp(Math.Abs(dy) / _screenRect.Height, .01, .5);
            LookWidthBox.Value = Math.Round(SelectedLook.MaxRadiusX * LogicalWidth * 2);
            LookRadiusYBox.Value = Math.Round(SelectedLook.MaxRadiusY * LogicalHeight * 2);
        }
        else if (_resizeTarget == "move")
        {
            SelectedMove.Radius = Math.Round(Math.Clamp(normalized, 0.02, 0.35), 3);
            RadiusBox.Value = SelectedMove.Radius;
        }
        else if (_selectedButtonId is { Length: > 0 } id)
        {
            var button = _profile.Buttons.FirstOrDefault(b => b.Id == id);
            if (button is not null)
            {
                button.Radius = Math.Round(Math.Clamp(normalized, 0.01, 0.3), 3);
                RadiusBox.Value = button.Radius;
            }
        }
        DrawLayout();
    }

    private void LayoutCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (PreviewPointerReleased(e)) return;
        if (!_editorMode)
            return;
        if (_resizingLayout)
        {
            _resizingLayout = false;
            _resizeTarget = null;
            LayoutCanvas.ReleasePointerCapture(e.Pointer);
            try
            {
                _profile?.Validate();
                SaveCollection();
            }
            catch (Exception error)
            {
                SetStatus(error.Message, true);
            }
            DrawLayout();
            e.Handled = true;
            return;
        }
        if (_draggingLayout)
        {
            _draggingLayout = false;
            _dragKind = null;
            LayoutCanvas.ReleasePointerCapture(e.Pointer);
            try
            {
                _profile?.Validate();
                SaveCollection();
            }
            catch (Exception error)
            {
                SetStatus(error.Message, true);
            }
            if (_selection == "move")
            {
                PopulateProperties("move");
            }
            else if (_selection == "look")
            {
                PopulateProperties("look");
            }
            else if (_selectedButtonId is { Length: > 0 } id)
            {
                PopulateProperties("button", id);
            }
            DrawLayout();
        }
    }

    private GamepadButton? HitTestControl(Point canvasPoint)
    {
        if (_profile is null)
        {
            return null;
        }
        foreach (var button in _profile.Buttons.AsEnumerable().Reverse())
        {
            var center = new Point(
                _screenRect.X + button.X * _screenRect.Width,
                _screenRect.Y + button.Y * _screenRect.Height);
            var radius = Math.Max(9,
                button.Radius * Math.Min(_screenRect.Width, _screenRect.Height));
            // A little forgiveness so a click that lands on the ring still
            // counts as the key instead of falling through to MouseLeft/fire.
            if (Distance(canvasPoint, center) <= radius + 14)
            {
                return button;
            }
        }
        return null;
    }

    private (string? Kind, string? ButtonId) HitTest(Point normalized)
    {
        if (_profile is null)
        {
            return (null, null);
        }
        foreach (var button in _profile.Buttons.AsEnumerable().Reverse())
        {
            var dx = (normalized.X - button.X) * _screenRect.Width;
            var dy = (normalized.Y - button.Y) * _screenRect.Height;
            var radius = Math.Max(9, button.Radius * Math.Min(_screenRect.Width, _screenRect.Height)) + 5;
            if (dx * dx + dy * dy <= radius * radius)
            {
                return ("button", button.Id);
            }
        }
        foreach (var (look, index) in _profile.Looks.Select((l, i) => (l, i)).Reverse())
            if (look.Enabled && Math.Abs(normalized.X - look.AnchorX) <= look.MaxRadiusX &&
                Math.Abs(normalized.Y - look.AnchorY) <= look.MaxRadiusY) return ("look", index.ToString());
        foreach (var (move, index) in _profile.Moves.Select((m, i) => (m, i)).Reverse())
        {
            var dx = (normalized.X - move.CenterX) * _screenRect.Width;
            var dy = (normalized.Y - move.CenterY) * _screenRect.Height;
            var radius = CanvasMoveRadius(move) * (1 + move.SprintExtra) + 5;
            if (move.Enabled && dx * dx + dy * dy <= radius * radius) return ("move", index.ToString());
        }
        return (null, null);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private Point ToNormalized(Point position)
    {
        if (_screenRect.Width <= 0 || _screenRect.Height <= 0)
        {
            return new Point(0, 0);
        }
        return new Point(
            Math.Clamp((position.X - _screenRect.X) / _screenRect.Width, 0, 1),
            Math.Clamp((position.Y - _screenRect.Y) / _screenRect.Height, 0, 1));
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandlePreviewKeyDown(e)) return;
        if (IsTextInputFocused())
        {
            return;
        }

        if (_editorMode)
        {
            if (_capturingBinding)
            {
                e.Handled = true;
                if (e.Key == VirtualKey.Escape)
                {
                    CancelBindingCapture();
                    SetStatus("已取消按键捕获");
                }
                else
                {
                    ApplyCapturedBinding(KeyName(e.Key));
                }
                return;
            }
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                _ = ToggleEditorAsync(false);
                return;
            }
            if (e.Key == VirtualKey.Z && IsControlDown())
            {
                e.Handled = true;
                Undo();
                return;
            }
            if (e.Key == VirtualKey.Y && IsControlDown())
            {
                e.Handled = true;
                Redo();
                return;
            }
            if (e.Key == VirtualKey.D && IsControlDown())
            {
                e.Handled = true;
                DuplicateSelected();
                return;
            }
            if (e.Key == VirtualKey.E && IsControlDown())
            {
                e.Handled = true;
                _ = ToggleEditorAsync(false);
                return;
            }
            if (e.Key is VirtualKey.Delete or VirtualKey.Back)
            {
                e.Handled = true;
                DeleteSelected();
                return;
            }
            if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            {
                e.Handled = true;
                NudgeSelected(e.Key, IsShiftDown());
            }
            return;
        }

        // The native keyboard hook owns gameplay events; XAML only handles editor/preview input.
        if (_engine.IsRunning) e.Handled = true;
    }

    private void Page_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_canvasPreview is not null || (_engine.IsRunning && !_editorMode)) e.Handled = true;
    }

    private void NudgeSelected(VirtualKey key, bool big)
    {
        if (_profile is null || _selection is null)
        {
            return;
        }
        var step = big ? 0.02 : 0.005;
        var dx = key == VirtualKey.Left ? -step : key == VirtualKey.Right ? step : 0;
        var dy = key == VirtualKey.Up ? -step : key == VirtualKey.Down ? step : 0;
        PushUndo();
        if (_selection == "move")
        {
            SelectedMove.CenterX = Math.Round(Math.Clamp(SelectedMove.CenterX + dx, 0, 1), 3);
            SelectedMove.CenterY = Math.Round(Math.Clamp(SelectedMove.CenterY + dy, 0, 1), 3);
            XBox.Value = SelectedMove.CenterX;
            YBox.Value = SelectedMove.CenterY;
        }
        else if (_selection == "look")
        {
            SelectedLook.AnchorX = Math.Round(Math.Clamp(SelectedLook.AnchorX + dx, 0, 1), 3);
            SelectedLook.AnchorY = Math.Round(Math.Clamp(SelectedLook.AnchorY + dy, 0, 1), 3);
            XBox.Value = SelectedLook.AnchorX;
            YBox.Value = SelectedLook.AnchorY;
        }
        else if (_selectedButtonId is { Length: > 0 } id)
        {
            var button = _profile.Buttons.FirstOrDefault(b => b.Id == id);
            if (button is null)
            {
                return;
            }
            button.X = Math.Round(Math.Clamp(button.X + dx, 0, 1), 3);
            button.Y = Math.Round(Math.Clamp(button.Y + dy, 0, 1), 3);
            XBox.Value = button.X;
            YBox.Value = button.Y;
        }
        SaveCollection();
        DrawLayout();
    }

    private static bool IsControlDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) &
         CoreVirtualKeyStates.Down) != CoreVirtualKeyStates.None;

    private static bool IsShiftDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) &
         CoreVirtualKeyStates.Down) != CoreVirtualKeyStates.None;

    private static string KeyName(VirtualKey key) => key switch
    {
        VirtualKey.Number0 => "0",
        VirtualKey.Number1 => "1",
        VirtualKey.Number2 => "2",
        VirtualKey.Number3 => "3",
        VirtualKey.Number4 => "4",
        VirtualKey.Number5 => "5",
        VirtualKey.Number6 => "6",
        VirtualKey.Number7 => "7",
        VirtualKey.Number8 => "8",
        VirtualKey.Number9 => "9",
        VirtualKey.LeftShift => "LeftShift",
        VirtualKey.RightShift => "RightShift",
        VirtualKey.Shift => "LeftShift",
        VirtualKey.LeftControl => "LeftControl",
        VirtualKey.RightControl => "RightControl",
        VirtualKey.Control => "LeftControl",
        VirtualKey.LeftMenu => "LeftAlt",
        VirtualKey.RightMenu => "RightAlt",
        VirtualKey.Menu => "LeftAlt",
        VirtualKey.Space => "Space",
        VirtualKey.Enter => "Enter",
        VirtualKey.Up => "Up",
        VirtualKey.Down => "Down",
        VirtualKey.Left => "Left",
        VirtualKey.Right => "Right",
        _ => key.ToString()
    };

    private bool IsTextInputFocused()
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        while (focused is DependencyObject node)
        {
            if (node is TextBox or PasswordBox or NumberBox or ComboBox or Slider or ToggleSwitch) return true;
            focused = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private static bool IsAppActive()
    {
        var foreground = NativeKeyboard.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }
        NativeKeyboard.GetWindowThreadProcessId(foreground, out var pid);
        return pid == Environment.ProcessId;
    }

    private static bool IsBindingDown(string binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            return false;
        }
        var text = binding.Trim();
        var upper = text.ToUpperInvariant();
        if (upper is "MOUSELEFT" or "MOUSERIGHT" or "MOUSEMIDDLE")
        {
            // Mouse buttons are injected by the native hook so that clicking the
            // page itself never fires a virtual key. Polling the physical
            // button here would turn every click on the toolbar into a shot.
            return false;
        }
        var alias = upper switch
        {
            "SHIFT" or "LEFTSHIFT" => 0x10,
            "RIGHTSHIFT" => 0xA1,
            "CONTROL" or "CTRL" or "LEFTCONTROL" => 0x11,
            "RIGHTCONTROL" => 0xA3,
            "ALT" or "LEFTALT" => 0x12,
            "RIGHTALT" => 0xA5,
            _ => 0
        };
        if (alias != 0)
        {
            return (NativeKeyboard.GetAsyncKeyState(alias) & 0x8000) != 0;
        }
        if (text.Length == 1 && char.IsLetterOrDigit(text[0]))
        {
            return (NativeKeyboard.GetAsyncKeyState(char.ToUpperInvariant(text[0])) & 0x8000) != 0;
        }
        if (Enum.TryParse<VirtualKey>(text, true, out var key))
        {
            return (NativeKeyboard.GetAsyncKeyState((int)key) & 0x8000) != 0;
        }
        return false;
    }

    private void SaveCollection()
    {
        if (!GamepadProfileStore.Save(_collection))
        {
            SetStatus("保存失败: " + (GamepadProfileStore.LastError ?? "未知错误"), true);
        }
    }
}
