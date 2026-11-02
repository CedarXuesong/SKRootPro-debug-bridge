using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SkpdbDesktop;

internal static class SnapshotTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        await using var peer = new Peer();
        using var client = new SkpdbClient();
        await client.ConnectAsync(peer.Options);
        var originalGeneration = client.ConnectionGeneration;
        var first = await ScreenSnapshotCapture.CaptureAsync(client);
        Check(first.Png.SequenceEqual(peer.Image) && first.Png.Length > 900 * 1024,
            "Multi-frame PNG was truncated or changed");
        Check(first.Width == 1024 && first.Height == 512 && first.Rotation == 0, "Wrong display metadata");
        var second = await ScreenSnapshotCapture.CaptureAsync(client);
        Check(first.Id != second.Id && peer.Paths.Distinct().Count() == 2 &&
            peer.Paths.All(path => path.Contains(first.Id[..8]) && path.EndsWith(".png")),
            "Captures need unique timestamped identifiers");
        Check(peer.CleanupCount == 2 && client.ConnectionGeneration == originalGeneration &&
            await client.PingAsync() == "PONG", "Capture interfered with primary connection or cleanup");
        Console.WriteLine("PASS memory-only multi-frame PNG, timestamped IDs, cleanup and independent connection");

        peer.FailCapture = true;
        await ExpectAsync<InvalidOperationException>(() => ScreenSnapshotCapture.CaptureAsync(client));
        peer.FailCapture = false;
        Check(peer.CleanupCount == 3 && await client.PingAsync() == "PONG", "Shell failure was not cleaned up");
        peer.Image = new byte[60];
        await ExpectAsync<InvalidDataException>(() => ScreenSnapshotCapture.CaptureAsync(client));
        peer.Image = first.Png[..^1];
        await ExpectAsync<InvalidDataException>(() => ScreenSnapshotCapture.CaptureAsync(client));
        peer.Image = first.Png;
        Console.WriteLine("PASS failed screencap, invalid PNG and truncated PNG rejected; primary stays usable");

        peer.DisplayReads = 0;
        peer.RotateOnce = true;
        var retried = await ScreenSnapshotCapture.CaptureAsync(client);
        Check(retried.Rotation == 2 && peer.DisplayReads == 4, "Rotation race should retry a stable frame");
        peer.RotateOnce = false;
        peer.AlwaysRotate = true;
        peer.DisplayReads = 0;
        await ExpectAsync<InvalidOperationException>(() => ScreenSnapshotCapture.CaptureAsync(client));
        Check(peer.DisplayReads == 4, "Unstable rotation retry must be bounded");
        peer.AlwaysRotate = false;
        Console.WriteLine("PASS rotation races retried once; continuously rotating display rejected");

        using (var cancel = new CancellationTokenSource())
        {
            peer.GateRead = true;
            var pending = ScreenSnapshotCapture.CaptureAsync(client, cancel.Token);
            await peer.ReadGated.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(await client.PingAsync().WaitAsync(TimeSpan.FromSeconds(1)) == "PONG", "Download blocked control socket");
            var previousCleanup = peer.CleanupCount;
            cancel.Cancel();
            await ExpectAsync<OperationCanceledException>(() => pending);
            Check(peer.CleanupCount == previousCleanup + 1 && await client.PingAsync() == "PONG",
                "Canceled read needs cleanup on a fresh channel without closing primary");
        }
        Console.WriteLine("PASS cancellation during download cleans up through a new channel; primary still responds");

        peer.ReadGated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.ReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.GateRead = true;
        var stale = ScreenSnapshotCapture.CaptureAsync(client);
        await peer.ReadGated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await client.ConnectAsync(peer.Options);
        peer.ReadRelease.TrySetResult();
        await ExpectAsync<OperationCanceledException>(() => stale);
        Check(await client.PingAsync() == "PONG", "Session replacement corrupted new connection");
        Console.WriteLine("PASS late snapshot from replaced session rejected");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    // A real PNG, including filtered rows, zlib data and CRCs, so the same peer
    // can exercise the actual WinUI image decoder during UI verification.
    private static byte[] MakePng()
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 1024);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 512);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[1 + 1024 * 3];
            var random = new Random(9124);
            for (var y = 0; y < 512; y++)
            {
                random.NextBytes(row.AsSpan(1));
                zlib.Write(row);
            }
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var bytes = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        uint crc = uint.MaxValue;
        foreach (var item in bytes)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        output.Write(word);
    }

    public static async Task RunUiPeerAsync()
    {
        await using var peer = new Peer();
        Console.WriteLine($"SNAPSHOT_UI_PEER host=127.0.0.1 port={peer.Options.Port} key=local-snapshot-test");
        await Task.Delay(Timeout.Infinite);
    }

    public static async Task RunBuildChannelAsync()
    {
        Check(BuildInfo.ShortCommit.Length == 12 &&
            BuildInfo.ShortCommit.All(Uri.IsHexDigit) &&
            BuildInfo.DisplayVersion == $"{BuildInfo.BaseVersion}+{BuildInfo.ShortCommit}",
            "Version must include a twelve-character source commit");
        await using var peer = new Peer();
        using var client = new SkpdbClient();
        await client.ConnectAsync(peer.Options);
        var legacy = await client.GetInfoAsync();
        Check(legacy.BuildChannel == "" && legacy.DebugOverlayEnabled, "Legacy module compatibility changed");
        Check(BuildInfo.UseDebugOverlay(legacy) == BuildInfo.IsCanary, "Legacy HUD policy is wrong");
        peer.InfoBuildChannel = "Release";
        var release = await client.GetInfoAsync();
        Check(release.BuildChannel == "Release" && !release.DebugOverlayEnabled &&
            !BuildInfo.UseDebugOverlay(release), "Release module must never start a HUD session");
        peer.InfoBuildChannel = "Canary";
        var canary = await client.GetInfoAsync();
        Check(canary.BuildChannel == "Canary" && canary.DebugOverlayEnabled &&
            BuildInfo.UseDebugOverlay(canary) == BuildInfo.IsCanary, "Canary HUD policy is wrong");
#if SKPDB_CANARY
        Check(BuildInfo.Channel == "Canary" && BuildInfo.IsCanary, "Canary constant missing");
#else
        Check(BuildInfo.Channel == "Release" && !BuildInfo.IsCanary, "Release constant missing");
#endif
        Console.WriteLine($"PASS {BuildInfo.Channel}: module metadata, mixed-channel HUD policy and legacy compatibility");
    }

    public static async Task RunOverlayAsync()
    {
        await using var peer = new Peer();
        using var client = new SkpdbClient();
        await client.ConnectAsync(peer.Options);
        var state = new ControlOverlayState(2400, 1080, 1, 8, 10, 4, 239.8, 2.4, 1, true);
        await client.ControlOverlayAsync(state, true);
        var packet = peer.HudPackets.Last();
        Check(packet.Length == 20 && packet[0] == 'C' && packet[1] == 3 && packet[2] == 1 && packet[3] == 1 &&
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)) == 2400 &&
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)) == 1080 &&
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8)) == 8 &&
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10)) == 10 &&
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(12)) == 4 &&
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14)) == 2398 &&
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16)) == 24 && packet[18] == 1 && packet[19] == 1,
            "HUD wire field order or endianness changed");
        await client.CursorSetAsync(20, 30, false); await client.CursorDestroyAsync();
        Check(peer.HudEnabled == 1, "Cursor visibility destroyed HUD");
        await client.ControlOverlayAsync(state, false);
        Check(peer.HudEnabled == 0, "Disabled HUD remains active");
        await ExpectAsync<ArgumentOutOfRangeException>(() => client.ControlOverlayAsync(state with { Width = 0 }, true));
        await ExpectAsync<ArgumentOutOfRangeException>(() => client.ControlOverlayAsync(state with { Rotation = 4 }, true));
        await ExpectAsync<ArgumentOutOfRangeException>(() => client.ControlOverlayAsync(state with { Active = 11 }, true));
        await ExpectAsync<ArgumentOutOfRangeException>(() => client.ControlOverlayAsync(state with { SamplingHz = double.NaN }, true));
        await ExpectAsync<ArgumentOutOfRangeException>(() => client.ControlOverlayAsync(state with { RttMs = double.PositiveInfinity }, true));
        Console.WriteLine("PASS HUD wire fields, cursor independence and invalid state validation");

        var session = await ControlOverlaySession.StartAsync(client, state);
        session.Update(state with { Rotation = 3, Mode = 0, InputActive = false });
        await Task.Delay(650);
        Check(peer.HudPackets.Last()[3] == 3 && peer.HudPackets.Last()[18] == 0 && peer.HudPackets.Last()[19] == 0,
            "Heartbeat did not send latest geometry/mode/focus");
        var slow = client.ExecAsync("fixture slow");
        var previous = peer.HudPackets.Count;
        await Task.Delay(600);
        Check(peer.HudPackets.Count > previous, "Primary channel blocks HUD heartbeat");
        await slow;
        await session.DisposeAsync();
        var stopped = peer.HudPackets.Count;
        await Task.Delay(600);
        Check(peer.HudEnabled == 0 && peer.HudPackets.Count == stopped && await client.PingAsync() == "PONG",
            "Stopped heartbeat wrote late state or disposed primary connection");
        Console.WriteLine("PASS HUD heartbeat, focus/mode/rotation updates, isolated transport and orderly stop");

        peer.HudSupported = false;
        await ExpectAsync<InvalidOperationException>(() => ControlOverlaySession.StartAsync(client, state));
        peer.HudSupported = true;
        session = await ControlOverlaySession.StartAsync(client, state);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Failed += error => failed.TrySetResult(error);
        peer.HudSupported = false;
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(session.Failure is not null, "Persistent heartbeat failure went unnoticed");
        await session.DisposeAsync();
        peer.HudSupported = true;
        Check(await client.PingAsync() == "PONG", "HUD failure closed primary connection");
        Console.WriteLine("PASS old module refusal, bounded reconnect and persistent failure notification");

        session = await ControlOverlaySession.StartAsync(client, state);
        var beforeRetry = peer.HudPackets.Count;
        Interlocked.Exchange(ref peer.HudDropNext, 1);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            while (peer.HudPackets.Count == beforeRetry) await Task.Delay(25, deadline.Token);
        Check(session.Failure is null && peer.HudEnabled == 1, "One dropped HUD socket did not recover");
        failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Failed += error => failed.TrySetResult(error);
        await client.ConnectAsync(peer.Options);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(session.Failure is not null && await client.PingAsync() == "PONG", "Stale HUD session continued after primary replacement");
        await session.DisposeAsync();
        Console.WriteLine("PASS one-shot socket failure recovery and stale primary-generation isolation");
    }

    internal sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = new();
        private readonly Task _accept;
        public ConnectionOptions Options { get; }
        public byte[] Image = MakePng();
        public bool FailCapture, RotateOnce, AlwaysRotate, GateRead;
        public int CleanupCount, DisplayReads, FixedRotation;
        public bool HudSupported = true;
        public string InfoBuildChannel = "";
        public int HudEnabled, HudUpdates, HudDropNext;
        public ConcurrentQueue<byte[]> HudPackets { get; } = new();
        private int _touchInfoReads;
        private int _slowCommands;
        private int _slowNextInfo;
        private readonly ConcurrentDictionary<int, bool> _active = new();
        private readonly ConcurrentDictionary<int, int> _downs = new();
        private readonly object _gestureGate = new();
        private TcpClient? _gestureOwner;
        private int _gestureEnabled;
        public bool GestureSupported = true;
        public int GestureEnabled => Volatile.Read(ref _gestureEnabled);
        public ConcurrentQueue<byte> GestureRequests { get; } = new();
        public ConcurrentBag<string> Paths { get; } = new();
        public TaskCompletionSource ReadGated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Peer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Options = new("127.0.0.1", port, port == 65535 ? port - 1 : port + 1, "local-snapshot-test");
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                    _clients.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[32], _stop.Token);
                await stream.ReadExactlyAsync(new byte[32], _stop.Token);
                while (!_stop.IsCancellationRequested)
                {
                    var header = new byte[4];
                    await stream.ReadExactlyAsync(header, _stop.Token);
                    var request = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
                    await stream.ReadExactlyAsync(request, _stop.Token);
                    var op = request[0];
                    byte[] body;
                    if (op == (byte)'K') body = Encoding.ASCII.GetBytes("PONG");
                    else if (op == (byte)'E')
                    {
                        var command = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                        var text = "";
                        var exit = 0;
                        if (command == "dumpsys display")
                        {
                            var n = Interlocked.Increment(ref DisplayReads);
                            var rotation = AlwaysRotate ? (n % 2 == 0 ? 2 : 0) : RotateOnce && n > 1 ? 2 : FixedRotation;
                            text = $"Viewport INTERNAL: orientation={rotation} logicalFrame=Rect(0, 0 - 1024, 512)";
                        }
                        else if (command.StartsWith("screencap -p ", StringComparison.Ordinal))
                        {
                            Paths.Add(command[13..]);
                            if (FailCapture) { exit = 1; text = "capture failed"; }
                        }
                        else if (command.StartsWith("rm -f ", StringComparison.Ordinal)) Interlocked.Increment(ref CleanupCount);
                        else if (command == "fixture fail") FailCapture = true;
                        else if (command == "fixture ok") { FailCapture = false; FixedRotation = 0; Image = MakePng(); }
                        else if (command == "fixture invalid") { Image = MakePng(); Image[41] = 0; Image[42] = 0; }
                        else if (command == "fixture rotate") FixedRotation = 2;
                        else if (command == "fixture slow")
                        { Interlocked.Increment(ref _slowCommands); await Task.Delay(350, _stop.Token); text = "slow-result"; }
                        else if (command == "fixture fast") text = "fast-result";
                        else if (command == "fixture slow-info") Interlocked.Exchange(ref _slowNextInfo, 1);
                        else if (command == "fixture hud-unsupported") HudSupported = false;
                        else if (command == "fixture hud-ok") HudSupported = true;
                        else if (command == "fixture state") text = System.Text.Json.JsonSerializer.Serialize(new
                        { touchInfoReads = _touchInfoReads, slowCommands = _slowCommands, active = _active.Keys.Order().ToArray(), downs = _downs,
                            hudEnabled = HudEnabled, hudUpdates = HudUpdates, hudPacket = HudPackets.LastOrDefault(),
                            gestureEnabled = GestureEnabled });
                        else if (command.Contains("dumpsys", StringComparison.Ordinal)) text = "mCurrentFocus=Window{123 u0 com.snapshot.test/.MainActivity}";
                        var encoded = Encoding.UTF8.GetBytes(text);
                        body = new byte[8 + encoded.Length];
                        BinaryPrimitives.WriteInt32BigEndian(body, exit);
                        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4), encoded.Length);
                        encoded.CopyTo(body, 8);
                    }
                    else if (op == (byte)'R')
                    {
                        if (GateRead)
                        {
                            GateRead = false;
                            ReadGated.TrySetResult();
                            await ReadRelease.Task.WaitAsync(_stop.Token);
                        }
                        var offset = (int)BinaryPrimitives.ReadUInt64BigEndian(request.AsSpan(1));
                        var amount = (int)BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(9));
                        var count = Math.Min(amount, Math.Max(0, Image.Length - offset));
                        body = new byte[4 + count];
                        BinaryPrimitives.WriteInt32BigEndian(body, count);
                        if (count > 0) Image.AsSpan(offset, count).CopyTo(body.AsSpan(4));
                    }
                    else if (op == (byte)'C')
                    {
                        if (Interlocked.Exchange(ref HudDropNext, 0) == 1) return;
                        if (request.Length == 20 && request[1] == 3 && HudSupported)
                        {
                            HudEnabled = request[2]; Interlocked.Increment(ref HudUpdates); HudPackets.Enqueue(request);
                            body = Encoding.ASCII.GetBytes(HudEnabled == 1 ? "hud=1" : "hud=0");
                        }
                        else body = Array.Empty<byte>();
                    }
                    else if (op == (byte)'J')
                    {
                        for (var i = 0; i < request[1]; ++i)
                        {
                            var offset = 2 + i * 12;
                            var slot = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(offset + 2));
                            if (request[offset] == 1) { _active[slot] = true; _downs.AddOrUpdate(slot, 1, (_, n) => n + 1); }
                            if (request[offset] == 3) _active.TryRemove(slot, out _);
                        }
                        body = Array.Empty<byte>();
                    }
                    else if (op == (byte)'L')
                    {
                        var path = Encoding.UTF8.GetString(request, 1, request.Length - 1);
                        if (path == "/slow") await Task.Delay(350, _stop.Token);
                        var listing = Encoding.UTF8.GetBytes("f\t" + path.Trim('/') + ".txt\n");
                        body = new byte[4 + listing.Length];
                        BinaryPrimitives.WriteInt32BigEndian(body, listing.Length);
                        listing.CopyTo(body, 4);
                    }
                    else if (op == (byte)'B' && request.Length == 3 && request[1] == 7)
                    {
                        body = Array.Empty<byte>();
                        lock (_gestureGate)
                        {
                            GestureRequests.Enqueue(request[2]);
                            if (GestureSupported && (_gestureOwner is null || ReferenceEquals(_gestureOwner, client)))
                            {
                                if (request[2] == 1 || request[2] == 2 && ReferenceEquals(_gestureOwner, client))
                                {
                                    _gestureOwner = client; Volatile.Write(ref _gestureEnabled, 1);
                                    body = Encoding.ASCII.GetBytes("gesture_lease=1\ngesture_changed=1\n");
                                }
                                else if (request[2] == 0)
                                {
                                    _gestureOwner = null; Volatile.Write(ref _gestureEnabled, 0);
                                    body = Encoding.ASCII.GetBytes("gesture_lease=0\ngesture_changed=0\n");
                                }
                            }
                        }
                    }
                    else if (op == (byte)'B')
                    {
                        if (Interlocked.Exchange(ref _slowNextInfo, 0) == 1) await Task.Delay(350, _stop.Token);
                        var channelInfo = InfoBuildChannel.Length == 0 ? "" :
                            "\nbuild_channel=" + InfoBuildChannel + "\ndebug_overlay=" + (InfoBuildChannel == "Canary" ? "1" : "0");
                        body = Encoding.UTF8.GetBytes("name=snapshot-test\nversion=0.0.0\nservice_port=" + Options.Port + channelInfo +
                            "\ngesture_lease=" + (GestureSupported ? "1" : "0"));
                    }
                    else if (op == (byte)'A')
                    {
                        var pid = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(1));
                        if (pid == 111) await Task.Delay(350, _stop.Token);
                        body = Encoding.UTF8.GetBytes("maps=" + pid);
                    }
                    else if (op == (byte)'G') body = Encoding.UTF8.GetBytes("backends=test");
                    else if (op == (byte)'I')
                    {
                        Interlocked.Increment(ref _touchInfoReads);
                        body = Encoding.UTF8.GetBytes("touch=ready\nname=snapshot-test\nscreen=1024x512\nslots=0..9\nbtn_touch=1\nbtn_tool_finger=1\n");
                    }
                    else body = Array.Empty<byte>();
                    var response = new byte[2 + body.Length];
                    response[0] = op;
                    body.CopyTo(response, 2);
                    BinaryPrimitives.WriteInt32BigEndian(header, response.Length);
                    await stream.WriteAsync(header, _stop.Token);
                    await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
            finally
            {
                lock (_gestureGate)
                    if (ReferenceEquals(_gestureOwner, client))
                    { _gestureOwner = null; Volatile.Write(ref _gestureEnabled, 0); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accept;
            await Task.WhenAll(_clients);
            _stop.Dispose();
        }
    }
}
