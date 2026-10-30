using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SkpdbDesktop;

internal static class ReconfigurationTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, deadline.Token);
    }

    public static async Task RunAsync()
    {
        await using var peer = new Peer();
        using var control = new SkpdbClient();
        await control.ConnectAsync(peer.Options);
        await using var engine = new GamepadEngine();
        var device = new TouchDeviceInfo("test", "", "", 1280, 2772, 9, true, true);
        var profile = GamepadProfileStore.CreateDefaultProfile();
        var log = new ConcurrentQueue<string>();
        engine.Log += (_, message) => log.Enqueue(message);
        // Use the real generation fence even without a UDP sender in this test.
        engine.ConfigureFastPath("127.0.0.1", 0, "", 2, 16);
        await engine.StartAsync(control, device, profile);
        var connections = peer.Connections;
        var stopCount = log.Count(s => s == "外设已停止");
        var startCount = log.Count(s => s.StartsWith("外设已启动"));
        for (var i = 0; i < 40; i++)
        {
            var viewMode = i % 2 == 1;
            engine.SetViewMode(viewMode);
            if (viewMode) { engine.SetKey("W", true); engine.AddMouseDelta(40, 20); }
            else engine.PointerTouchDown(.4, .5);
            await UntilAsync(() => peer.ActiveCount > 0);
            // Old queued clicks and keys must not leak into the new app.
            if (!viewMode) { engine.PointerTouchUp(); engine.PointerTouchDown(.9, .9); }
            var next = profile.Clone();
            next.Package = "test.app" + i;
            var rotated = device.WithDisplay(i % 4, i % 2 == 0 ? 1280 : 2772, i % 2 == 0 ? 2772 : 1280);
            await engine.ReconfigureAsync(rotated, next).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(45);
            Check(engine.IsRunning && engine.ViewMode == viewMode, "Configuration switch lost running/mode state");
            Check(engine.ActivePackage == next.Package, "Latest profile was not applied");
            Check(peer.ActiveCount == 0, "An old finger or queued DOWN crossed the configuration boundary");
            Check(engine.Metrics.ActiveSlots == 0, "Mouse/view mode indicator was counted as a held finger");
        }
        Check(peer.Connections == connections, "Reconfiguration reopened input TCP");
        Check(log.Count(s => s == "外设已停止") == stopCount &&
            log.Count(s => s.StartsWith("外设已启动")) == startCount, "Reconfiguration stopped/restarted the engine");
        Check(peer.Streams.Distinct().Count() == 1 && peer.Generations.SequenceEqual(peer.Generations.Order()),
            "Reconfiguration reset the stream/generation fence");
        Console.WriteLine("PASS 40 app/rotation switches keep one input TCP, stream and mode, release old touches and discard queued DOWNs");

        // Pending transitions and explicit stop are serialized, even while the
        // phone is still acknowledging an old finger's release.
        engine.SetKey("W", false); engine.SetKey("W", true);
        await UntilAsync(() => peer.ActiveCount > 0);
        peer.GateNextRelease = true;
        var pending = engine.ReconfigureAsync(device, profile);
        await peer.ReleaseGated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopped = engine.StopAsync();
        Check(!stopped.IsCompleted, "Stop raced the configuration release");
        peer.ReleaseProceed.TrySetResult();
        await Task.WhenAll(pending, stopped).WaitAsync(TimeSpan.FromSeconds(5));
        Check(!engine.IsRunning && peer.ActiveCount == 0, "Serialized stop left active touches");
        Console.WriteLine("PASS stop waits for an accepted configuration transition");

        await engine.StartAsync(control, device, profile);
        engine.SetKey("W", true);
        await UntilAsync(() => peer.ActiveCount > 0);
        peer.RejectNextRelease = true;
        try
        {
            await engine.ReconfigureAsync(device, profile).WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception("Expected release failure");
        }
        catch (InvalidOperationException e) when (e.Message.Contains("release rejected")) { }
        Check(!engine.IsRunning && engine.Metrics.LastError?.Contains("release rejected") == true,
            "Failed release falsely reported a successful switch");
        Check(await control.PingAsync() == "PONG", "Reconfiguration failure closed control TCP");
        Console.WriteLine("PASS failed release rejects configuration, stops safely and preserves control TCP");

        // Stop the watcher while it is reading a framed shell reply. A shared
        // channel used to cancel the main session here.
        await using var watcher = new ForegroundWatcher(control);
        peer.StallQuery = true;
        watcher.Start();
        await peer.QueryGated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await watcher.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(control.IsConnected && await control.PingAsync() == "PONG", "Stopping watcher closed control TCP");
        peer.StallQuery = false;
        peer.Package = "test.foreground";
        watcher.Start();
        await UntilAsync(() => watcher.Package == peer.Package);
        peer.Package = "test.latest";
        await UntilAsync(() => watcher.Package == peer.Package);
        await watcher.StopAsync();
        Check(await control.PingAsync() == "PONG", "Restarted watcher corrupted main session");
        Console.WriteLine("PASS in-flight watcher cancellation, restart and latest foreground detection preserve control TCP");
    }

    internal sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = new();
        private readonly ConcurrentDictionary<int, bool> _slots = new();
        private readonly Task _accept;
        public int Connections;
        public int ActiveCount => _slots.Count;
        public readonly ConcurrentQueue<uint> Streams = new();
        public readonly ConcurrentQueue<uint> Generations = new();
        public readonly ConcurrentQueue<TouchBatchEvent> Events = new();
        public TaskCompletionSource? BatchEntered, BatchProceed;
        public bool RejectNextRelease, GateNextRelease, StallQuery;
        public string Package = "test.start";
        public TaskCompletionSource ReleaseGated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseProceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource QueryGated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConnectionOptions Options { get; }

        public Peer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Options = new("127.0.0.1", port, port == 65535 ? port - 1 : port + 1, "local-test");
            _accept = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref Connections);
                    _clients.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) { }
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
                    var frame = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
                    await stream.ReadExactlyAsync(frame, _stop.Token);
                    var op = frame[0];
                    byte[] body = [];
                    string? error = null;
                    if (op == (byte)'K') body = Encoding.ASCII.GetBytes("PONG");
                    if (op == (byte)'E')
                    {
                        if (StallQuery)
                        {
                            QueryGated.TrySetResult();
                            await Task.Delay(Timeout.Infinite, _stop.Token);
                        }
                        var output = Encoding.UTF8.GetBytes($"topResumedActivity=ActivityRecord{{123 u0 {Package}/.Main t1}}\n");
                        body = new byte[8].Concat(output).ToArray();
                        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4, 4), output.Length);
                    }
                    if (op == (byte)'B' && frame[1] == 5)
                    {
                        var count = frame[10];
                        var events = Enumerable.Range(0, count).Select(i => new TouchBatchEvent(
                            frame[11 + i * 12],
                            BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(15 + i * 12, 4)),
                            BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(19 + i * 12, 4)),
                            BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(13 + i * 12, 2)))).ToArray();
                        var releasing = events.Any(e => e.Action == 3) && events.All(e => e.Action is 2 or 3);
                        if (releasing && GateNextRelease)
                        {
                            GateNextRelease = false;
                            ReleaseGated.TrySetResult();
                            await ReleaseProceed.Task.WaitAsync(_stop.Token);
                        }
                        if (releasing && RejectNextRelease) { RejectNextRelease = false; error = "release rejected"; }
                        else
                        {
                            Streams.Enqueue(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(2, 4)));
                            Generations.Enqueue(BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(6, 4)));
                            foreach (var item in events)
                            {
                                Events.Enqueue(item);
                                if (item.Action == 1) _slots[item.Slot] = true;
                                if (item.Action == 3) _slots.TryRemove(item.Slot, out _);
                            }
                        }
                        if (BatchProceed is { } proceed)
                        {
                            BatchProceed = null;
                            BatchEntered?.TrySetResult();
                            await proceed.Task.WaitAsync(_stop.Token);
                        }
                    }
                    byte[] response;
                    if (error is null) response = new byte[] { op, 0 }.Concat(body).ToArray();
                    else
                    {
                        var message = Encoding.UTF8.GetBytes(error);
                        response = new byte[6 + message.Length];
                        response[0] = op; response[1] = 1;
                        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(2, 4), message.Length);
                        message.CopyTo(response, 6);
                    }
                    BinaryPrimitives.WriteInt32BigEndian(header, response.Length);
                    await stream.WriteAsync(header, _stop.Token);
                    await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
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
