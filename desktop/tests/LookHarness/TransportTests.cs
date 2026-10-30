using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SkpdbDesktop;

internal static class TransportTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        await using var server = new Peer();
        using var client = new SkpdbClient();
        await client.ConnectAsync(server.Options);
        try { await client.ExecAsync("reject"); throw new Exception("Expected application rejection"); }
        catch (InvalidOperationException e) when (e.Message == "rejected") { }
        Check(client.IsConnected && (await client.PingAsync()) == "PONG", "Application errors must preserve framing");
        Console.WriteLine("PASS application error preserves connection");
        var pending = client.ExecAsync("gate");
        await server.Gated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (var queued = new CancellationTokenSource(50))
        {
            try { await client.PingAsync(queued.Token); throw new Exception("Expected queue cancellation"); }
            catch (OperationCanceledException) { }
        }
        Check(client.IsConnected, "Canceling a queued request must not close an active request");
        server.Proceed.TrySetResult();
        await pending;
        Check((await client.PingAsync()) == "PONG", "Queue cancellation corrupted reply");
        Console.WriteLine("PASS queued cancellation preserves in-flight reply");
        foreach (var mode in new[] { "close", "invalid", "wrong-opcode", "partial" })
        {
            await client.ConnectAsync(server.Options);
            try { await client.ExecAsync(mode); throw new Exception("Expected failed frame"); }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
            Check(!client.IsConnected, mode + " must invalidate the connection");
            Console.WriteLine("PASS " + mode + " invalidates failed stream");
        }
        await client.ConnectAsync(server.Options);
        await using var engine = new GamepadEngine();
        var device = new TouchDeviceInfo("test", "", "", 1280, 2772, 9, true, true);
        var profile = GamepadProfileStore.CreateDefaultProfile();
        await engine.StartAsync(client, device, profile);
        server.StallBatch = true;
        engine.PointerTouchDown(.4, .5);
        await server.BatchStalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // In production this is the same three-second reliable deadline. The
        // old catch swallowed it and left IsRunning true after its loop exited.
        for (int i = 0; i < 100 && (engine.IsRunning || server.ReleaseCount == 0); i++) await Task.Delay(50);
        Check(!engine.IsRunning, "Timed-out sampler still claims to run");
        Check(engine.Metrics.LastError?.Contains("3 秒") == true, "Timeout reason was swallowed");
        Check(server.ReleaseCount > 0, "Disconnected input channel did not release using an independent cleanup channel");
        Check(client.IsConnected && (await client.PingAsync()) == "PONG", "Input timeout closed the control connection");
        Console.WriteLine("PASS reliable timeout stops sampler, reports reason and releases through a fresh channel");
        await engine.StartAsync(client, device, profile);
        engine.PointerTouchDown(.4, .5);
        await Task.Delay(60);
        var previousReleases = server.ReleaseCount;
        server.StallBatch = true;
        engine.PointerTouchUp();
        for (int i = 0; i < 100 && (engine.IsRunning || server.ReleaseCount == previousReleases); i++) await Task.Delay(50);
        Check(!engine.IsRunning && server.ReleaseCount > previousReleases,
            "A timed-out UP must release slots even after local state is already up");
        Console.WriteLine("PASS lost UP reply releases all reserved slots");
        await engine.StartAsync(client, device, profile);
        engine.PointerTouchDown(.4, .5);
        await Task.Delay(60);
        await engine.StopAsync();
        Check(engine.Metrics.LastError is null && !engine.IsRunning, "Restart/normal stop retained failure");
        Console.WriteLine("PASS restart after timeout and normal stop");
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = new();
        private readonly Task _accept;
        public TaskCompletionSource Gated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BatchStalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StallBatch;
        public int ReleaseCount;
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
            try { while (!_stop.IsCancellationRequested) _clients.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token))); }
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
                    var command = op == (byte)'E' ? Encoding.UTF8.GetString(frame, 1, frame.Length - 1) : "";
                    if (command == "close") return;
                    if (command == "invalid") { await stream.WriteAsync(new byte[] { 0, 0, 0, 1 }, _stop.Token); return; }
                    if (command == "partial") { await stream.WriteAsync(new byte[] { 0, 0, 0, 8, op }, _stop.Token); return; }
                    if (command == "gate") { Gated.TrySetResult(); await Proceed.Task.WaitAsync(_stop.Token); }
                    byte[] response;
                    if (command == "reject")
                        response = new byte[] { op, 1, 0, 0, 0, 8 }.Concat(Encoding.UTF8.GetBytes("rejected")).ToArray();
                    else if (op == (byte)'K') response = new byte[] { op, 0 }.Concat(Encoding.ASCII.GetBytes("PONG")).ToArray();
                    else if (op == (byte)'J')
                    {
                        if (StallBatch)
                        {
                            StallBatch = false;
                            BatchStalled.TrySetResult();
                            await Task.Delay(6000, _stop.Token);
                            return;
                        }
                        for (var i = 0; i < frame[1]; i++)
                            if (frame[2 + i * 12] == 3) Interlocked.Increment(ref ReleaseCount);
                        response = new byte[] { op, 0 };
                    }
                    else response = new byte[] { command == "wrong-opcode" ? (byte)'Z' : op, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
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
