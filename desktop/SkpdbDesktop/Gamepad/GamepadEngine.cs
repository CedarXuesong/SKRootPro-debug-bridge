using System.Diagnostics;

namespace SkpdbDesktop;

public sealed record GamepadMetrics(
    double RttMs,
    double EventsPerSecond,
    int ActiveSlots,
    long TotalEvents,
    string? LastError,
    double SamplingHz = 0,
    double SampleP95Ms = 0,
    long MissedSamples = 0,
    int SlotCapacity = 0,
    int PendingSlots = 0);

public sealed class GamepadEngine : IAsyncDisposable
{
    private sealed class ButtonRuntime
    {
        public required GamepadButton Config { get; init; }
        public bool WasPressed { get; set; }
        public bool Down { get; set; }
        public required SampledButtonAction Action { get; init; }
    }

    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SampledInputState _keys = new();
    private readonly SampledInputState _controls = new(canonicalKeys: false);
    private bool _inputEnabled = true, _pendingInputReset, _resetMouseControls;
    private readonly Dictionary<string, (double X, double Y)> _controlVectors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Id, int Direction)> _wheelPulses = new();
    public TouchTraceBuffer TouchTrace { get; } = new();
    public bool FastConfigured
    {
        get
        {
            lock (_gate)
            {
                return _fastUdp is not null;
            }
        }
    }
    private readonly Dictionary<string, ButtonRuntime> _buttons =
        new(StringComparer.OrdinalIgnoreCase);

    private SkpdbClient? _client;
    private SkpdbClient? _controlClient;
    private TouchDeviceInfo? _device;
    private GamepadProfile? _profile;
    private TouchSlotAllocator? _slotAllocator;
    private System.Net.Sockets.UdpClient? _fastUdp;
    private System.Net.IPEndPoint? _fastEndpoint;
    private byte[]? _fastToken;
    private int _fastProtocol = 1;
    private int _fastMaxRecords = 8;
    private uint _touchStream;
    private uint _touchGeneration;
    private uint _touchSequence;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private sealed record ConfigurationChange(TouchDeviceInfo Device, GamepadProfile Profile,
        TaskCompletionSource Completion);
    private ConfigurationChange? _pendingConfiguration;
    private bool _timerPeriodSet;

    private bool _running;
    private readonly SampledJoystick _moveStick = new();
    private bool _moveDown => _moveStick.IsDown;
    private readonly LookTouchRelay _lookRelay = new();
    private bool _lookDown => _lookRelay.IsDown;
    private bool _viewMode;
    private bool _observeDown;
    private bool _pendingLookRelease, _pendingPointerRelease;
    private double _lookAccumX;
    private double _lookAccumY;
    // Framework gyro injection mode: mouse deltas become a yaw/pitch rate that
    // is sent to the module's SKG1 fast path (~240 Hz), not a touch look.
    private bool _gyroMode;
    private double _gyroSensitivity = 0.0015;  // rad/s per pixel, per 240 Hz tick
    private uint _gyroSequence;
    private GyroSession? _gyroSession;
    private readonly GyroMotion _gyroMotion = new();
    private double _gyroLastSample;
    private double _lastTcpMove = double.NegativeInfinity;
    private uint _cursorSequence;
    private enum PointerTouchAction : byte { Down = 1, Move = 2, Up = 3 }

    private readonly LinkedList<(PointerTouchAction Action, double X, double Y)> _pointerTouchQueue = new();
    private bool _pointerTouchAvailable;
    private ushort _pointerTouchSlot;
    private bool _pointerTouchStarted;
    private double _pointerTouchDownAt;
    private double _pointerTouchX;
    private double _pointerTouchY;
    private int _pointerTouchNativeX;
    private int _pointerTouchNativeY;

    private readonly Stopwatch _uptime = new();
    private readonly Queue<double> _rttSamples = new();
    private readonly Queue<double> _sampleIntervals = new();
    private long _lastSampleAt, _samples, _samplesAtWindow, _missedSamples;
    private double _samplingHz;
    private long _totalEvents;
    private long _eventsAtWindow;
    private double _windowStart;
    private double _eventsPerSecond;
    private string? _lastError;

    public GamepadMetrics Metrics
    {
        get
        {
            lock (_gate)
            {
                var rtt = 0.0;
                if (_rttSamples.Count > 0)
                {
                    var ordered = _rttSamples.OrderBy(value => value).ToArray();
                    rtt = ordered[ordered.Length / 2];
                }
                return new GamepadMetrics(
                    rtt,
                    _eventsPerSecond,
                    ActiveSlots(),
                    _totalEvents,
                    _lastError,
                    _samplingHz,
                    _sampleIntervals.Count == 0 ? 0 :
                        _sampleIntervals.Order().ElementAt((int)((_sampleIntervals.Count - 1) * .95)),
                    _missedSamples,
                    _slotAllocator?.Capacity ?? 0,
                    _slotAllocator?.PendingCount ?? 0);
            }
        }
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public string? ActivePackage
    {
        get { lock (_gate) return _profile?.Package; }
    }

    public bool IsLooking
    {
        get { lock (_gate) return _lookDown || _viewMode || _observeDown; }
    }

    public bool ViewMode
    {
        get { lock (_gate) return _viewMode; }
    }

    /// <summary>True while the cursor is free to move (not looking).</summary>
    public bool IsMouseMode
    {
        get { lock (_gate) return !_viewMode && !_observeDown; }
    }

    /// <summary>True when direct mouse input is configured; physical slots are leased on press.</summary>
    public bool DirectTouchAvailable
    {
        get { lock (_gate) return _pointerTouchAvailable; }
    }

    /// <summary>
    /// Supplies the current normalized phone-screen cursor while a direct
    /// touch is held. The engine polls it every tick; this does not depend on
    /// WinUI PointerMoved events after the canvas captures the mouse.
    /// </summary>
    public Func<(double X, double Y)?>? PointerPositionProvider { get; set; }

    public void ToggleViewMode() => SetViewMode(!ViewMode);

    public void SetViewMode(bool enabled)
    {
        bool changed;
        lock (_gate)
        {
            changed = SetViewModeLocked(enabled);
        }
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool SetViewModeLocked(bool value)
    {
        if (_viewMode == value)
        {
            return false;
        }
        _viewMode = value;
        CancelMouseBindingsLocked();
        // Do not apply movement that was collected for the previous mode.
        _lookAccumX = 0;
        _lookAccumY = 0;
        if (value)
        {
            // The direct tap belongs to mouse mode; lift it before the look
            // finger takes over.
            _pointerTouchQueue.Clear();
            _pendingPointerRelease = true;
        }
        if (!value)
        {
            _observeDown = false;
            // Leaving view mode has to lift the look finger now. Depending on
            // RecenterMode it might otherwise stay down until the engine stops,
            // leaving a stuck camera touch on the phone.
            _pendingLookRelease = true;
        }
        return true;
    }

    private void CancelMouseBindingsLocked()
    {
        _keys.Cancel(mouseOnly: true);
        foreach (var runtime in _buttons.Values.Where(b => SampledInputState.IsMouse(b.Config.Binding)))
            _controls.Cancel(runtime.Config.Id);
        _resetMouseControls = true;
    }

    public event EventHandler? Changed;
    public event EventHandler<string>? Log;

    /// <summary>Per-batch diagnostics; the page writes it to gamepad.log.</summary>
    public event EventHandler<string>? Trace;

    /// <summary>Reserve a logical mouse contact; device slots are leased on DOWN.</summary>
    private void ChoosePointerSlot(GamepadProfile profile)
    {
        var used = profile.Buttons.Select(b => b.Slot).ToHashSet();
        used.Add(profile.Move.Slot);
        used.Add(profile.Look.Slot);
        used.Add(_lookRelay.SpareSlot);
        _pointerTouchSlot = (ushort)Enumerable.Range(0, ushort.MaxValue + 1).First(s => !used.Contains(s));
        _pointerTouchAvailable = true;
    }

    public async Task StartAsync(SkpdbClient client, TouchDeviceInfo device,
        GamepadProfile profile, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await StartCoreAsync(client, device, profile, cancellationToken).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(SkpdbClient client, TouchDeviceInfo device,
        GamepadProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile.Clone();
        ValidateConfiguration(device, profile);

        await StopCoreAsync().ConfigureAwait(false);
        // Shell/dumpsys/file requests use the caller's socket. Only input uses
        // this channel, including reliable down/up and direct mouse dragging.
        var inputClient = await client.OpenIndependentChannelAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _timerPeriodSet = timeBeginPeriod(1) == 0;
            lock (_gate)
            {
                _client = inputClient;
                _controlClient = client;
                _device = device;
                _profile = profile;
                _slotAllocator = new TouchSlotAllocator(device.SlotMax);
                var samplingDevice = TouchSlotAllocator.SamplingDevice(device);
                _moveStick.Configure(samplingDevice, profile.Move);
                _lookRelay.Configure(samplingDevice, profile, allowOverlap: device.SlotMax > 0);
                ChoosePointerSlot(profile);
                _touchStream = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
                _touchGeneration = _touchSequence = 0;
                _buttons.Clear();
                foreach (var button in profile.Buttons)
                {
                    _buttons[button.Id] = CreateButtonRuntime(button, samplingDevice, profile.Move);
                }
                _keys.Reset();
                _controls.Reset();
                _inputEnabled = true;
                _pendingInputReset = _resetMouseControls = false;
                _controlVectors.Clear();
                _wheelPulses.Clear();
                _viewMode = false;
                _observeDown = false;
                _pendingLookRelease = _pendingPointerRelease = false;
                _lookAccumX = _lookAccumY = 0;
                _pointerTouchQueue.Clear();
                _pointerTouchStarted = false;
                _pointerTouchDownAt = 0;
                _pointerTouchX = _pointerTouchY = 0;
                _rttSamples.Clear();
                _sampleIntervals.Clear();
                _lastSampleAt = _samples = _samplesAtWindow = _missedSamples = 0;
                _samplingHz = 0;
                _uptime.Restart();
                _totalEvents = 0;
                _eventsAtWindow = 0;
                _windowStart = 0;
                _lastTcpMove = double.NegativeInfinity;
                _eventsPerSecond = 0;
                _lastError = null;
                _running = true;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = _cts.Token;
                _loop = Task.Factory.StartNew(() => RunLoop(token), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            Log?.Invoke(this, $"外设已启动：{profile.DisplayName}");
            Trace?.Invoke(this,
                $"LOOK logical primary={profile.Look.Slot} alt={_lookRelay.SpareSlot} pointer={(_pointerTouchAvailable ? _pointerTouchSlot.ToString() : "none")} dev=0..{device.SlotMax} {device.DisplayWidth}x{device.DisplayHeight}");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            inputClient.Dispose();
            throw;
        }
    }

    private static void ValidateConfiguration(TouchDeviceInfo device, GamepadProfile profile)
    {
        profile.Validate();
        if (device.SlotMax < 0 || device.SlotMax > ushort.MaxValue || device.DisplayWidth < 1 || device.DisplayHeight < 1)
            throw new InvalidOperationException("触摸屏尺寸或 slot 无效");
        new LookTouchRelay().Configure(TouchSlotAllocator.SamplingDevice(device), profile);
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Changes mappings at a sampling boundary. The input socket, UDP stream,
    /// sampling thread and explicit mouse/view mode survive app/rotation changes.
    /// Once accepted, the transition completes before another lifecycle action.
    /// </summary>
    public async Task ReconfigureAsync(TouchDeviceInfo device, GamepadProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(profile);
        var snapshot = profile.Clone();
        ValidateConfiguration(device, snapshot);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (!_running || _cts?.IsCancellationRequested != false)
                    throw new InvalidOperationException("操控已停止，无法切换配置");
                if (_client?.IsConnected != true)
                    throw new InvalidOperationException("触摸连接已断开，无法切换配置");
                _pendingConfiguration = new(device, snapshot, completion);
            }
            await completion.Task.ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ApplyPendingConfigurationAsync()
    {
        ConfigurationChange? change;
        lock (_gate)
        {
            change = _pendingConfiguration;
        }
        if (change is null) return;
        if(_gyroMode && !string.Equals(_profile?.Package,change.Profile.Package,StringComparison.Ordinal))
        {
            SkpdbClient? parent;uint owned;
            lock(_gate){parent=_controlClient;owned=_gyroSession?.Stream??0;_gyroMode=false;_gyroSession=null;_gyroMotion.Reset();}
            if(parent is not null&&owned!=0){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));using var channel=await parent.OpenIndependentChannelAsync(timeout.Token).ConfigureAwait(false);await channel.GyroDisarmAsync(owned,timeout.Token).ConfigureAwait(false);}
            Log?.Invoke(this,"应用已切换，陀螺仪已归零；新应用需重新确认启用");
        }
        // The ACK advances the existing stream generation, fencing out UDP
        // moves that were in flight for the old app or screen orientation.
        await ReleaseAllAsync(force: true, throwOnError: true, preserveViewMode: true).ConfigureAwait(false);
        lock (_gate)
        {
            _device = change.Device;
            _profile = change.Profile;
            _slotAllocator = new TouchSlotAllocator(change.Device.SlotMax);
            var samplingDevice = TouchSlotAllocator.SamplingDevice(change.Device);
            _moveStick.Configure(samplingDevice, change.Profile.Move);
            _lookRelay.Configure(samplingDevice, change.Profile, allowOverlap: change.Device.SlotMax > 0);
            ChoosePointerSlot(change.Profile);
            _buttons.Clear();
            foreach (var button in change.Profile.Buttons)
                _buttons[button.Id] = CreateButtonRuntime(button, samplingDevice, change.Profile.Move);
            // Discard input collected while the old mappings were being lifted.
            _keys.Cancel();
            _controls.Cancel();
            _controlVectors.Clear();
            _wheelPulses.Clear();
            _pointerTouchQueue.Clear();
            _lookAccumX = _lookAccumY = 0;
            _pendingConfiguration = null;
        }
        Trace?.Invoke(this, $"CONFIG {change.Profile.Package} {change.Device.DisplayWidth}x{change.Device.DisplayHeight} rotation={change.Device.Rotation} stream={_touchStream} generation={_touchGeneration}");
        change.Completion.TrySetResult();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            _running = false;
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        cts?.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        cts?.Dispose();
        SkpdbClient? gyroParent;
        uint ownedGyro;
        lock(_gate){gyroParent=_controlClient;ownedGyro=_gyroSession?.Stream??0;_gyroMode=false;_gyroSession=null;_gyroMotion.Reset();_gyroLastSample=0;}
        if(ownedGyro!=0&&gyroParent is {IsConnected:true})
        {
            try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));using var channel=await gyroParent.OpenIndependentChannelAsync(timeout.Token).ConfigureAwait(false);await channel.GyroDisarmAsync(ownedGyro,timeout.Token).ConfigureAwait(false);}
            catch(Exception error){Log?.Invoke(this,"陀螺仪可靠归零未确认，等待模块租期到期："+error.Message);}
        }
        await ReleaseAllAsync().ConfigureAwait(false);
        SkpdbClient? inputClient;
        lock (_gate)
        {
            inputClient = _client;
            _client = null;
            _controlClient = null;
        }
        inputClient?.Dispose();
        if (_timerPeriodSet)
        {
            TimeEndPeriod(1);
            _timerPeriodSet = false;
        }
        Log?.Invoke(this, "外设已停止");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetKey(string key, bool down)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        lock (_gate)
        {
            _keys.Set(key, down);
            if (!_inputEnabled || (SampledInputState.IsMouse(key) && IsMouseMode))
                _keys.Cancel(mouseOnly: _inputEnabled);
        }
    }

    public void AddMouseDelta(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        lock (_gate)
        {
            if (!_inputEnabled || IsMouseMode) return;
            _lookAccumX += dx;
            _lookAccumY += dy;
        }
    }

    public bool GyroMode
    {
        get { lock (_gate) return _gyroMode; }
    }

    public double GyroSensitivity
    {
        get { lock (_gate) return _gyroSensitivity; }
        set
        {
            if (!double.IsFinite(value)) return;
            lock (_gate) _gyroSensitivity = Math.Clamp(value, 0.0005, 0.02);
        }
    }

    /// <summary>
    /// Enables the framework gyro path. While active, view-mode mouse deltas are
    /// converted to a rate and sent over the SKG1 fast path; the touch look is
    /// released so the two input paths never fight.
    /// </summary>
    public async Task SetGyroModeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SkpdbClient parent;string package;
            lock(_gate){if(_gyroMode==enabled)return;parent=_controlClient??throw new InvalidOperationException("请先开始操控");package=_profile?.Package??"";}
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(TimeSpan.FromSeconds(12));
            using var channel=await parent.OpenIndependentChannelAsync(deadline.Token).ConfigureAwait(false);
            GyroSession? session=null;
            if(enabled)
            {
                lock(_gate)if(_fastUdp is null||_fastEndpoint is null||_fastToken is null)throw new InvalidOperationException("陀螺仪需要 UDP 快速通道");
                var uid=await channel.ResolvePackageUidAsync(package,deadline.Token).ConfigureAwait(false);
                session=await channel.GyroInstallAsync(uid,deadline.Token).ConfigureAwait(false);
            }
            else
            {
                uint owned;lock(_gate){owned=_gyroSession?.Stream??0;_gyroSession=null;_gyroMotion.Reset();}
                try{if(owned!=0)await channel.GyroDisarmAsync(owned,deadline.Token).ConfigureAwait(false);}
                catch{await Task.Delay(220).ConfigureAwait(false);lock(_gate)_gyroMode=false;throw;}
            }
            lock(_gate){_gyroSession=session;_gyroMode=enabled;_gyroMotion.Reset();_gyroLastSample=0;_lookAccumX=_lookAccumY=0;if(enabled){_pendingLookRelease=true;_lastError=null;}}
            if(session is not null)_=MonitorGyroAsync(parent,session,_cts?.Token??CancellationToken.None);
        }
        finally{_lifecycle.Release();}
    }

    private async Task MonitorGyroAsync(SkpdbClient parent,GyroSession session,CancellationToken token)
    {
        try
        {
            using var channel=await parent.OpenIndependentChannelAsync(token).ConfigureAwait(false);
            while(true)
            {
                await Task.Delay(500,token).ConfigureAwait(false);
                lock(_gate)if(!ReferenceEquals(_gyroSession,session))return;
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await channel.CheckGyroAsync(session.Stream,timeout.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
        catch(Exception error)
        {
            lock(_gate)
            {
                if(!ReferenceEquals(_gyroSession,session))return;
                _gyroSession=null;_gyroMotion.Reset();_lookAccumX=_lookAccumY=0;
            }
            await Task.Delay(220).ConfigureAwait(false);
            lock(_gate){if(_gyroSession is not null)return;_gyroMode=false;_lastError="陀螺仪已停用并恢复触摸视角："+error.Message;}
            Log?.Invoke(this,_lastError!);Changed?.Invoke(this,EventArgs.Empty);
        }
    }

    private void SendGyroRate(double rx, double ry, double rz = 0)
    {
        System.Net.Sockets.UdpClient? udp;
        System.Net.IPEndPoint? endpoint;
        byte[]? token;
        uint stream;
        lock (_gate)
        {
            udp = _fastUdp;
            endpoint = _fastEndpoint;
            token = _fastToken;
            stream = _gyroSession?.Stream ?? 0;
        }
        if (udp is null || endpoint is null || token is null || stream == 0) return;

        rx = Math.Clamp(rx, -15.0, 15.0);
        ry = Math.Clamp(ry, -15.0, 15.0);
        Span<byte> packet = stackalloc byte[40];
        packet[0] = (byte)'S';
        packet[1] = (byte)'K';
        packet[2] = (byte)'G';
        packet[3] = (byte)'2';
        token.AsSpan().CopyTo(packet.Slice(4, 16));
        var sequence = Interlocked.Increment(ref _gyroSequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(20, 4), stream);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(24, 4), sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(28, 4),
            BitConverter.SingleToUInt32Bits((float)rx));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(32, 4),
            BitConverter.SingleToUInt32Bits((float)ry));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(36, 4),
            BitConverter.SingleToUInt32Bits((float)Math.Clamp(rz,-15,15)));
        try
        {
            udp.Client.SendTo(packet, endpoint);
        }
        catch
        {
            // The next tick retries; a dropped sample is harmless.
        }
    }

    public void SetMouseButton(string button, bool down) => SetKey(button, down);

    /// <summary>
    /// Starts a direct touch at a normalized phone-screen point. This is the
    /// mouse-mode "click where the cursor is" path, including control overlays.
    /// </summary>
    private void QueuePointerLocked((PointerTouchAction Action, double X, double Y) value)
    {
        // Coalesce only adjacent MOVE samples; DOWN/UP endpoints retain order.
        if (value.Action == PointerTouchAction.Move && _pointerTouchQueue.Last is { } last &&
            last.Value.Action == PointerTouchAction.Move) last.Value = value;
        else _pointerTouchQueue.AddLast(value);
    }

    private bool PeekPointerLocked(out (PointerTouchAction Action, double X, double Y) value)
    {
        value = _pointerTouchQueue.First?.Value ?? default;
        return _pointerTouchQueue.First is not null;
    }

    private bool DequeuePointerLocked(out (PointerTouchAction Action, double X, double Y) value)
    {
        if (!PeekPointerLocked(out value)) return false;
        _pointerTouchQueue.RemoveFirst(); return true;
    }

    public void PointerTouchDown(double normalizedX, double normalizedY)
    {
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY)) return;
        lock (_gate)
        {
            if (!_running || !_inputEnabled || _viewMode || _observeDown || !_pointerTouchAvailable)
                return;
            _pointerTouchX = Math.Clamp(normalizedX, 0, 1);
            _pointerTouchY = Math.Clamp(normalizedY, 0, 1);
            QueuePointerLocked((PointerTouchAction.Down,
                _pointerTouchX, _pointerTouchY));
        }
    }

    public void PointerTouchMove(double normalizedX, double normalizedY)
    {
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY)) return;
        lock (_gate)
        {
            if (!_pointerTouchAvailable)
                return;
            QueuePointerLocked((PointerTouchAction.Move,
                Math.Clamp(normalizedX, 0, 1), Math.Clamp(normalizedY, 0, 1)));
        }
    }

    public void PointerTouchUp()
    {
        lock (_gate)
        {
            if (!_pointerTouchAvailable)
                return;
            // Queue the lift instead of debouncing it: the next press can arrive
            // immediately and must not be dropped while this lift is pending.
            QueuePointerLocked((PointerTouchAction.Up, 0, 0));
        }
    }

    public void PointerTouchUp(double normalizedX, double normalizedY)
    {
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY)) { PointerTouchUp(); return; }
        lock (_gate)
        {
            if (!_pointerTouchAvailable)
                return;
            // Freeze the API endpoint and its UP together. The sampling
            // thread must not poll a later cursor position between them.
            QueuePointerLocked((PointerTouchAction.Move,
                Math.Clamp(normalizedX, 0, 1), Math.Clamp(normalizedY, 0, 1)));
            QueuePointerLocked((PointerTouchAction.Up, 0, 0));
        }
    }

    /// <summary>
    /// Moves the direct touch by a normalized delta. Used when the page has
    /// Raw Input but WinUI pointer-move events are not arriving during capture.
    /// </summary>
    public void PointerTouchMoveBy(double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY)) return;
        lock (_gate)
        {
            if (!_pointerTouchAvailable)
                return;
            _pointerTouchX = Math.Clamp(_pointerTouchX + deltaX, 0, 1);
            _pointerTouchY = Math.Clamp(_pointerTouchY + deltaY, 0, 1);
            QueuePointerLocked((PointerTouchAction.Move,
                _pointerTouchX, _pointerTouchY));
        }
    }

    public void SetControlPressed(string id, bool down)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }
        lock (_gate)
        {
            _controls.Set(id, down);
            if (!_inputEnabled) _controls.Cancel();
            if (!down) _controlVectors.Remove(id);
        }
    }

    public void SetControlVector(string id, double x, double y)
    {
        if (string.IsNullOrWhiteSpace(id) || !double.IsFinite(x) || !double.IsFinite(y)) return;
        lock (_gate)
        {
            _controlVectors[id] = (Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1));
        }
    }

    public void PulseControl(string id, int direction)
    {
        if (string.IsNullOrWhiteSpace(id) || direction == 0)
        {
            return;
        }
        lock (_gate)
        {
            if (!_inputEnabled || !_running) return;
            _wheelPulses.Add((id, Math.Sign(direction)));
        }
    }

    public void ConfigureFastPath(string host, int port, string tokenHex, int protocol = 1, int maxRecords = 8)
    {
        lock (_gate)
        {
            _fastUdp?.Dispose();
            _fastUdp = null;
            _fastEndpoint = null;
            _fastToken = null;
            _fastProtocol = protocol;
            _fastMaxRecords = Math.Clamp(maxRecords, 1, protocol >= 2 ? 16 : 8);
            if (port <= 0 || tokenHex.Length != 32 ||
                !System.Net.IPAddress.TryParse(host, out var address))
            {
                return;
            }
            try
            {
                _fastToken = Convert.FromHexString(tokenHex);
                _fastEndpoint = new System.Net.IPEndPoint(address, port);
                _fastUdp = new System.Net.Sockets.UdpClient();
                _fastUdp.Client.SendBufferSize = 64 * 1024;
            }
            catch
            {
                _fastUdp?.Dispose();
                _fastUdp = null;
                _fastEndpoint = null;
                _fastToken = null;
            }
        }
    }

    public bool SendCursorMove(int x, int y)
    {
        System.Net.Sockets.UdpClient? udp;
        System.Net.IPEndPoint? endpoint;
        byte[]? token;
        lock (_gate)
        {
            udp = _fastUdp;
            endpoint = _fastEndpoint;
            token = _fastToken;
        }
        if (udp is null || endpoint is null || token is null)
        {
            return false;
        }

        Span<byte> packet = stackalloc byte[32];
        packet[0] = (byte)'S';
        packet[1] = (byte)'K';
        packet[2] = (byte)'C';
        packet[3] = (byte)'1';
        token.AsSpan().CopyTo(packet.Slice(4, 16));
        var sequence = Interlocked.Increment(ref _cursorSequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.Slice(20, 4), sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(24, 2),
            (ushort)Math.Clamp(x, 0, 65535));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.Slice(26, 2),
            (ushort)Math.Clamp(y, 0, 65535));
        try
        {
            udp.Client.SendTo(packet, endpoint);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void ClearInput()
    {
        lock (_gate)
        {
            _keys.Cancel();
            _controls.Cancel();
            _controlVectors.Clear();
            _wheelPulses.Clear();
            _pointerTouchQueue.Clear();
            _observeDown = false;
            _pendingInputReset = true;
            _lookAccumX = _lookAccumY = 0;
        }
    }

    public void SetInputEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_inputEnabled == enabled) return;
            _inputEnabled = enabled;
            ClearInput();
            if (!enabled) SetViewModeLocked(false);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _fastUdp?.Dispose();
            _fastUdp = null; _fastEndpoint = null; _fastToken = null;
        }
    }

    private void RunLoop(CancellationToken cancellationToken)
    {
        Thread.CurrentThread.Name ??= "SKPDB touch 240Hz";
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }
        try
        {
            using var clock = new PrecisionSampleClock();
            while (!cancellationToken.IsCancellationRequested)
            {
                clock.WaitNext(cancellationToken);
                ApplyPendingConfigurationAsync().GetAwaiter().GetResult();
                var now = Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    if (_lastSampleAt != 0)
                        _sampleIntervals.Enqueue((now - _lastSampleAt) * 1000.0 / Stopwatch.Frequency);
                    while (_sampleIntervals.Count > 960) _sampleIntervals.Dequeue();
                    _lastSampleAt = now;
                    _samples++;
                    _missedSamples = clock.MissedDeadlines;
                }
                TickAsync(cancellationToken).GetAwaiter().GetResult();
                UpdateStats();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _lastError = error.Message;
                _running = false;
            }
            Trace?.Invoke(this, "input loop failed: " + error);
            Log?.Invoke(this, "外设异常: " + error.Message);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            ReleaseAllAsync(force: true).GetAwaiter().GetResult();
            SkpdbClient? inputClient;
            lock (_gate)
            {
                _running = false;
                inputClient = _client;
                _client = null;
                _pendingConfiguration?.Completion.TrySetException(
                    new InvalidOperationException(_lastError ?? "采样线程已停止，配置未切换"));
                _pendingConfiguration = null;
                if (_timerPeriodSet) { timeEndPeriod(1); _timerPeriodSet = false; }
            }
            inputClient?.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        SkpdbClient client;
        TouchDeviceInfo device;
        GamepadProfile profile;
        lock (_gate)
        {
            client = _client ?? throw new InvalidOperationException("外设未初始化");
            device = _device ?? throw new InvalidOperationException("触摸屏未初始化");
            profile = _profile ?? throw new InvalidOperationException("配置未加载");
        }

        var batch = new List<TouchBatchEvent>(8);
        lock (_gate)
        {
            if (_pendingInputReset)
            {
                ReleaseTouchesLocked(batch, clearPointerQueue: false);
                _pendingInputReset = false;
            }
            _keys.Advance(); _controls.Advance();
            UpdateModes();
            // Interrupt the old direct touch before the new mode requests
            // contacts, bypassing ordinary tap duration during mode changes.
            if (_pendingPointerRelease)
            {
                ReleasePointerTouchLocked(batch);
                _pendingPointerRelease = false;
            }
            UpdateMove(device, profile, batch);
            UpdateButtons(device, profile, batch);
            UpdateLook(device, profile, batch);
            UpdatePointerTouch(device, batch);
            UpdateWheelPulses(device, profile, batch);
        }
        // Keep the 240 Hz stream clocked even when integer coordinates repeat
        // (slow mouse motion or a held button). These are state snapshots;
        // evdev naturally suppresses unchanged coordinates, without inventing
        // movement. The phone's short buffer must not re-prime every pixel.
        if (batch.Count > 0 || _moveDown || _lookRelay.IsDown || _pointerTouchStarted ||
            _buttons.Values.Any(RuntimeHasTouch))
        {
            // A down/up batch that carries only the new finger makes some touch
            // drivers drop the other fingers, because the kernel entry no longer
            // mentions them. Re-state every finger that is still held, so look,
            // move and fire all travel in the same sampling round.
            batch = WithActiveTouches(device, profile, batch);
            bool forceReliable;
            lock (_gate)
            {
                // SKF2 rejects stale/duplicate packets across reliable
                // transitions. Continuous direct drags can therefore use UDP
                // without waiting for one TCP round trip per sample.
                forceReliable = (_fastProtocol < 2 || _fastUdp is null) && (_pointerTouchStarted ||
                    (_pointerTouchAvailable && batch.Any(item => item.Slot == _pointerTouchSlot)));
            }
            lock (_gate) batch = _slotAllocator!.Process(batch);
            if (batch.Count > 0)
                await SendBatchAsync(client, batch, forceReliable, cancellationToken).ConfigureAwait(false);
        }
    }

    private void UpdateMove(TouchDeviceInfo device, GamepadProfile profile,
        List<TouchBatchEvent> batch)
    {
        var up = Pressed(profile.Bindings.GetValueOrDefault("MoveUp", "W"));
        var down = Pressed(profile.Bindings.GetValueOrDefault("MoveDown", "S"));
        var left = Pressed(profile.Bindings.GetValueOrDefault("MoveLeft", "A"));
        var right = Pressed(profile.Bindings.GetValueOrDefault("MoveRight", "D"));
        // Holding the sprint key pushes the same stick farther out, which is
        // what a real joystick does when the player runs.
        var sprint = Pressed(profile.Bindings.GetValueOrDefault("Sprint", profile.Move.SprintKey));
        var dx = (right ? 1 : 0) - (left ? 1 : 0);
        var dy = (down ? 1 : 0) - (up ? 1 : 0);

        if (!_moveStick.IsDown && (dx != 0 || dy != 0))
            _moveStick.Configure(TouchSlotAllocator.SamplingDevice(device), profile.ChooseMove(Random.Shared));
        _moveStick.Update(dx, dy, sprint, Stopwatch.GetElapsedTime(0).TotalMilliseconds, batch);
    }

    private void UpdateLook(TouchDeviceInfo device, GamepadProfile profile,
        List<TouchBatchEvent> batch)
    {
        double dx, dy;
        bool releaseRequested;
        lock (_gate)
        {
            releaseRequested = _pendingLookRelease;
            _pendingLookRelease = false;
            dx = _lookAccumX; dy = _lookAccumY;
            _lookAccumX = _lookAccumY = 0;
        }
        if (_gyroMode)
        {
            // Release any look finger first, then turn the same mouse delta into
            // an angular rate. dx is pixels per sampling tick; 240 Hz converts it
            // back to px/s before applying the sensitivity.
            if (_lookRelay.HasGesture || releaseRequested)
                _lookRelay.Update(0, 0, Stopwatch.GetElapsedTime(0).TotalMilliseconds,
                    false, false, true, batch);
            var now=Stopwatch.GetElapsedTime(0).TotalSeconds;
            var elapsed=_gyroLastSample==0?1.0/240:Math.Max(now-_gyroLastSample,.0001);_gyroLastSample=now;
            if(elapsed>.2){_gyroMotion.Reset();dx=dy=0;}
            var rate=_gyroMotion.Step(dx,dy,elapsed,_gyroSensitivity*profile.Look.Sensitivity,device.Rotation,profile.Look.InvertY,profile.Look.Smoothing);
            if(releaseRequested||!_inputEnabled){_gyroMotion.Reset();rate=(0,0,0);}
            SendGyroRate(rate.X,rate.Y,rate.Z);
            return;
        }
        if (!_lookRelay.HasGesture && !releaseRequested && (dx != 0 || dy != 0 || _observeDown))
            _lookRelay.Configure(TouchSlotAllocator.SamplingDevice(device), profile, profile.ChooseLook(Random.Shared),
                allowOverlap: device.SlotMax > 0, preserveRemainder: true);
        _lookRelay.Update(dx, dy, Stopwatch.GetElapsedTime(0).TotalMilliseconds,
            _viewMode, _observeDown, releaseRequested, batch);
    }

    private void UpdatePointerTouch(TouchDeviceInfo device, List<TouchBatchEvent> batch)
    {
        lock (_gate)
        {
            if (!_pointerTouchAvailable)
                return;
            if (_viewMode || _observeDown)
            {
                // Late releases from the page belong to the old mouse gesture.
                _pointerTouchQueue.Clear();
                return;
            }
            // One queued event per sample. A fast click's down and up therefore
            // land in separate kernel batches instead of being collapsed into a
            // zero-duration tap, and the next click is never debounced away.
            var nowMs = Stopwatch.GetElapsedTime(0).TotalMilliseconds;
            if (_pointerTouchStarted &&
                PeekPointerLocked(out var pendingUp) &&
                pendingUp.Action == PointerTouchAction.Up &&
                nowMs - _pointerTouchDownAt < 40)
            {
                // Give every tap at least two display frames on the phone.
                // Without this a very fast click can be too short for the
                // browser/game to dispatch even though evdev saw it.
                return;
            }
            if (!DequeuePointerLocked(out var ev))
            {
                if (_pointerTouchStarted && PointerPositionProvider is { } provider)
                {
                    var position = provider();
                    if (position is { } p)
                    {
                        var nx = Math.Clamp(p.X, 0, 1);
                        var ny = Math.Clamp(p.Y, 0, 1);
                        if (Math.Abs(nx - _pointerTouchX) > 0.0002 ||
                            Math.Abs(ny - _pointerTouchY) > 0.0002)
                        {
                            var native = ToNative(device, nx, ny);
                            _pointerTouchX = nx;
                            _pointerTouchY = ny;
                            if (native.X != _pointerTouchNativeX || native.Y != _pointerTouchNativeY)
                            {
                                _pointerTouchNativeX = native.X;
                                _pointerTouchNativeY = native.Y;
                                batch.Add(new TouchBatchEvent(2, native.X, native.Y,
                                    _pointerTouchSlot));
                            }
                        }
                    }
                }
                return;
            }

            switch (ev.Action)
            {
                case PointerTouchAction.Down:
                    if (_pointerTouchStarted)
                    {
                        // Defensive: an old finger with no queued lift must not
                        // make the new press disappear.
                        batch.Add(new TouchBatchEvent(3, 0, 0, _pointerTouchSlot));
                        _pointerTouchStarted = false;
                    }
                    var down = ToNative(device, ev.X, ev.Y);
                    _pointerTouchX = ev.X;
                    _pointerTouchY = ev.Y;
                    _pointerTouchNativeX = down.X;
                    _pointerTouchNativeY = down.Y;
                    _pointerTouchStarted = true;
                    _pointerTouchDownAt = nowMs;
                    batch.Add(new TouchBatchEvent(1, down.X, down.Y, _pointerTouchSlot));
                    Trace?.Invoke(this,
                        $"DIRECT down logical={_pointerTouchSlot} {down.X},{down.Y}");
                    break;

                case PointerTouchAction.Move:
                    if (!_pointerTouchStarted)
                        return;
                    var move = ToNative(device, ev.X, ev.Y);
                    _pointerTouchX = ev.X;
                    _pointerTouchY = ev.Y;
                    if (move.X == _pointerTouchNativeX && move.Y == _pointerTouchNativeY)
                        return;
                    _pointerTouchNativeX = move.X;
                    _pointerTouchNativeY = move.Y;
                    batch.Add(new TouchBatchEvent(2, move.X, move.Y, _pointerTouchSlot));
                    break;

                case PointerTouchAction.Up:
                    if (!_pointerTouchStarted)
                        return;
                    // Final position and lift share the reliable generation.
                    // A delayed UDP move cannot erase the endpoint or revive
                    // the finger after it has been released.
                    batch.Add(new TouchBatchEvent(2, _pointerTouchNativeX,
                        _pointerTouchNativeY, _pointerTouchSlot));
                    _pointerTouchStarted = false;
                    batch.Add(new TouchBatchEvent(3, 0, 0, _pointerTouchSlot));
                    Trace?.Invoke(this, $"DIRECT up logical={_pointerTouchSlot}");
                    break;
            }
        }
    }

    private void UpdateModes()
    {
        var toggle = false;
        var observe = false;
        foreach (var runtime in _buttons.Values)
        {
            if (runtime.Config.Kind is not (GamepadControlKind.Look or GamepadControlKind.Observe)) continue;
            var pressed = _inputEnabled && (_controls.Pressed(runtime.Config.Id) || Pressed(runtime.Config.Binding));
            if (runtime.Config.Kind == GamepadControlKind.Look)
                toggle |= pressed && !runtime.WasPressed;
            else observe |= pressed;
            runtime.WasPressed = pressed;
            runtime.Down = pressed;
        }
        if (toggle) SetViewModeLocked(!_viewMode);
        var observeChanged = _observeDown != observe;
        if (observeChanged)
        {
            _observeDown = observe;
            CancelMouseBindingsLocked();
            _lookAccumX = _lookAccumY = 0;
            if (!observe && !_viewMode) _pendingLookRelease = true;
            _pointerTouchQueue.Clear();
            _pendingPointerRelease = true;
        }
        foreach (var runtime in _buttons.Values.Where(b => b.Config.Kind == GamepadControlKind.Look))
            runtime.Down = _viewMode;
        if (toggle || observeChanged) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateButtons(TouchDeviceInfo device, GamepadProfile profile,
        List<TouchBatchEvent> batch)
    {
        var now = _uptime.Elapsed.TotalMilliseconds;
        foreach (var runtime in _buttons.Values)
        {
            var config = runtime.Config;
            if (config.Kind is GamepadControlKind.Look or GamepadControlKind.Observe) continue;
            if (SampledInputState.IsMouse(config.Binding) && (_resetMouseControls || IsMouseMode))
                ReleaseButtonTouch(runtime, batch);
            var pressed = _inputEnabled && (!SampledInputState.IsMouse(config.Binding) || !IsMouseMode) &&
                (_controls.Pressed(config.Id) || Pressed(config.Binding));
            (double X, double Y)? vector = _controlVectors.TryGetValue(config.Id, out var stored) ? stored : null;
            runtime.Action.Update(pressed, now, batch, vector);
        }
        _resetMouseControls = false;
    }

    private static void ReleaseButtonTouch(ButtonRuntime runtime, List<TouchBatchEvent> batch)
    {
        runtime.Action.Release(batch);
        runtime.Down = runtime.WasPressed = false;
    }

    private static ButtonRuntime CreateButtonRuntime(GamepadButton button, TouchDeviceInfo device,
        GamepadMoveSettings motion) => new()
    {
        Config = button,
        Action = new SampledButtonAction(device, button, motion)
    };

    private void UpdateWheelPulses(TouchDeviceInfo device, GamepadProfile profile,
        List<TouchBatchEvent> batch)
    {
        List<(string Id, int Direction)> pulses;
        lock (_gate)
        {
            if (_wheelPulses.Count == 0)
            {
                return;
            }
            pulses = new List<(string, int)>(_wheelPulses);
            _wheelPulses.Clear();
        }
        foreach (var (id, direction) in pulses)
        {
            if (_buttons.TryGetValue(id, out var runtime))
            {
                runtime.Action.Pulse(direction);
            }
        }
    }

    /// <summary>
    /// Adds a move event for every finger that remains held but is not already
    /// part of this tick. Sending the complete set in one kernel entry keeps
    /// move, look and fire alive together.
    /// </summary>
    private List<TouchBatchEvent> WithActiveTouches(TouchDeviceInfo device,
        GamepadProfile profile, List<TouchBatchEvent> batch)
    {
        var slots = new HashSet<ushort>();
        foreach (var item in batch)
        {
            if (item.Action != 3)
                slots.Add(item.Slot);
        }

        var result = new List<TouchBatchEvent>(batch);
        void AddMove(int slot, int x, int y)
        {
            var key = (ushort)slot;
            if (!slots.Add(key))
                return;
            result.Add(new TouchBatchEvent(2, x, y, key));
        }

        if (_moveStick.HeldTouch is { } moveTouch)
            AddMove(moveTouch.Slot, moveTouch.X, moveTouch.Y);

        foreach (var touch in _lookRelay.HeldTouches())
            AddMove(touch.Slot, touch.X, touch.Y);

        lock (_gate)
        {
            if (_pointerTouchStarted)
                AddMove(_pointerTouchSlot, _pointerTouchNativeX, _pointerTouchNativeY);
        }

        foreach (var runtime in _buttons.Values)
            if (runtime.Action.HeldTouch is { } held) AddMove(held.Slot, held.X, held.Y);

        return result;
    }

    private async Task SendBatchAsync(SkpdbClient client, List<TouchBatchEvent> batch,
        bool forceReliable, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        System.Net.Sockets.UdpClient? fastUdp;
        System.Net.IPEndPoint? fastEndpoint;
        byte[]? fastToken;
        lock (_gate)
        {
            fastUdp = _fastUdp;
            fastEndpoint = _fastEndpoint;
            fastToken = _fastToken;
        }

        var hasReliable = batch.Any(item => item.Action != 2);
        if (hasReliable)
        {
            Trace?.Invoke(this, "TX " + string.Join(" ",
                batch.Select(item => $"{item.Action}:{item.Slot}@{item.X},{item.Y}")));
        }
        var useFast = !forceReliable && !hasReliable &&
                      fastUdp is not null && fastEndpoint is not null &&
                      fastToken is not null;
        var useTcp = !useFast;
        if (useTcp && fastUdp is null && !hasReliable && !forceReliable)
        {
            // A UDP-fast-path failure must never turn into a TCP request per
            // sample: that exhausts the bridge's per-connection threads.
            lock (_gate)
            {
                var now = _uptime.Elapsed.TotalMilliseconds;
                if (now - _lastTcpMove < 100)
                    return;
                _lastTcpMove = now;
            }
        }

        try
        {
            if (useFast && fastUdp is not null && fastEndpoint is not null &&
                fastToken is not null)
            {
                // Every held finger is present in this packet, so a look or move
                // update cannot silently replace the fire finger.
                for (var offset = 0; offset < batch.Count; offset += _fastMaxRecords)
                {
                    var count = Math.Min(_fastMaxRecords, batch.Count - offset);
                    var header = _fastProtocol >= 2 ? 33 : 21;
                    var packet = new byte[header + count * 12];
                    packet[0] = (byte)'S';
                    packet[1] = (byte)'K';
                    packet[2] = (byte)'F';
                    packet[3] = _fastProtocol >= 2 ? (byte)'2' : (byte)'1';
                    fastToken.CopyTo(packet, 4);
                    if (_fastProtocol >= 2)
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20, 4), _touchStream);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24, 4), _touchGeneration);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28, 4), ++_touchSequence);
                    }
                    packet[header - 1] = (byte)count;
                    for (var i = 0; i < count; i++)
                    {
                        var item = batch[offset + i];
                        var baseOffset = header + i * 12;
                        packet[baseOffset] = 2;
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(
                            packet.AsSpan(baseOffset + 2, 2), item.Slot);
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                            packet.AsSpan(baseOffset + 4, 4), (uint)Math.Max(0, item.X));
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                            packet.AsSpan(baseOffset + 8, 4), (uint)Math.Max(0, item.Y));
                    }
                    await fastUdp.SendAsync(packet, fastEndpoint, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                // Stop waits for a reliable transition to finish, then sends
                // all lifts. Canceling its reply midway corrupts the TCP stream.
                using var reliable = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                // A down/up and every held finger go in one kernel entry. Splitting
                // them into two-frame requests makes some drivers release the
                // fingers that the later entry does not mention.
                for (var offset = 0; offset < batch.Count; offset += 32)
                {
                    var count = Math.Min(32, batch.Count - offset);
                    string report;
                    if (_fastProtocol >= 2)
                    {
                        report = await client.TouchStreamBatchAsync(_touchStream, ++_touchGeneration,
                            batch.GetRange(offset, count), reliable.Token).ConfigureAwait(false);
                        _touchSequence = 0;
                    }
                    else
                        report = await client.TouchBatchAsync(batch.GetRange(offset, count), reliable.Token).ConfigureAwait(false);
                    if (hasReliable)
                        Trace?.Invoke(this, $"ACK {watch.Elapsed.TotalMilliseconds:F2}ms " + report.Trim().Replace('\n', ' '));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException error) when (useTcp)
        {
            throw new TimeoutException("独立触摸连接等待响应超过 3 秒，操控已停止，请重新开始", error);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _lastError = error.Message;
            }
            watch.Stop();
            throw;
        }
        watch.Stop();
        if (_device is { } traceDevice) TouchTrace.Record(traceDevice, batch);
        lock (_gate)
        {
            if (useTcp)
            {
                _rttSamples.Enqueue(watch.Elapsed.TotalMilliseconds);
                while (_rttSamples.Count > 24)
                {
                    _rttSamples.Dequeue();
                }
            }
            _totalEvents += batch.Count;
        }
    }

    private async Task ReleaseAllAsync(bool force = false, bool throwOnError = false,
        bool preserveViewMode = false)
    {
        SkpdbClient? client;
        SkpdbClient? control;
        var batch = new List<TouchBatchEvent>();
        lock (_gate)
        {
            client = _client;
            control = _controlClient;
            ReleaseTouchesLocked(batch);
            _observeDown = _pendingLookRelease = _pendingPointerRelease = false;
            if (!preserveViewMode) _viewMode = false;
            _keys.Cancel();
            _controls.Cancel();
            _controlVectors.Clear();
            _wheelPulses.Clear();
            _lookAccumX = _lookAccumY = 0;
            // Logical releases reset the samplers; only leased physical slots
            // may cross the wire. Force also retries uncertain earlier UPs.
            if (_slotAllocator is { } allocator)
            {
                batch = allocator.Process(batch, promotePending: false);
                foreach (var lift in allocator.ReleaseAll(force))
                    if (!batch.Any(e => e.Action == 3 && e.Slot == lift.Slot)) batch.Add(lift);
            }
            else batch.Clear();
        }
        if (client is null || batch.Count == 0) return;
        SkpdbClient? cleanup = null;
        try
        {
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // Reopen only for cleanup; never replay a DOWN after an uncertain
            // reply. Background shell work cannot delay these emergency lifts.
            if (!client.IsConnected && control is { IsConnected: true })
                client = cleanup = await control.OpenIndependentChannelAsync(release.Token).ConfigureAwait(false);
            if (_fastProtocol >= 2)
                await client.TouchStreamBatchAsync(_touchStream, ++_touchGeneration, batch, release.Token).ConfigureAwait(false);
            else
                await client.TouchBatchAsync(batch, release.Token).ConfigureAwait(false);
            if (_device is { } traceDevice) TouchTrace.Record(traceDevice, batch);
        }
        catch (Exception error)
        {
            Trace?.Invoke(this, "touch release failed: " + error.Message);
            if (throwOnError) throw;
        }
        finally { cleanup?.Dispose(); }
    }

    private void ReleaseTouchesLocked(List<TouchBatchEvent> batch, bool clearPointerQueue = true)
    {
        _moveStick.Release(batch);
        _lookRelay.Release(batch);
        ReleasePointerTouchLocked(batch);
        if (clearPointerQueue) _pointerTouchQueue.Clear();
        _pendingPointerRelease = false;
        foreach (var runtime in _buttons.Values)
        {
            ReleaseButtonTouch(runtime, batch);
            runtime.WasPressed = false;
        }
    }

    private void ReleasePointerTouchLocked(List<TouchBatchEvent> batch)
    {
        if (_pointerTouchStarted) batch.Add(new TouchBatchEvent(3, 0, 0, _pointerTouchSlot));
        _pointerTouchStarted = false;
        _pointerTouchDownAt = 0;
    }

    private static bool RuntimeHasTouch(ButtonRuntime runtime) => runtime.Action.IsDown;

    private static (int X, int Y) ToNative(TouchDeviceInfo device, double normalizedX,
        double normalizedY)
    {
        var logicalX = (int)Math.Round(normalizedX * Math.Max(0, device.DisplayWidth - 1));
        var logicalY = (int)Math.Round(normalizedY * Math.Max(0, device.DisplayHeight - 1));
        return device.ToNative(logicalX, logicalY);
    }

    private bool Pressed(string binding)
    {
        lock (_gate)
        {
            return _inputEnabled && (!SampledInputState.IsMouse(binding) || !IsMouseMode) && _keys.Pressed(binding);
        }
    }

    private int ActiveSlots() => _slotAllocator?.ActiveCount ?? 0;

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private static void TimeEndPeriod(uint period) => timeEndPeriod(period);

    private void UpdateStats()
    {
        lock (_gate)
        {
            var now = _uptime.Elapsed.TotalSeconds;
            var elapsed = now - _windowStart;
            if (elapsed >= 0.5)
            {
                _eventsPerSecond = (_totalEvents - _eventsAtWindow) / elapsed;
                _samplingHz = (_samples - _samplesAtWindow) / elapsed;
                _samplesAtWindow = _samples;
                _eventsAtWindow = _totalEvents;
                _windowStart = now;
            }
        }
    }
}


