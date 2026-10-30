using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SkpdbDesktop;

internal static class ClientLifecycleTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Fails(Task task, Type expected)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (Exception error) when (expected.IsInstanceOfType(error)) { return; }
        throw new InvalidOperationException("Expected " + expected.Name);
    }

    public static async Task RunAsync()
    {
        await using (var peer = new Peer())
        using (var client = new SkpdbClient())
        {
            peer.GateHandshake = 1;
            var connecting = client.ConnectAsync(peer.Options);
            await peer.HandshakeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!client.IsConnected, "Unverified handshake was published as connected");
            await Fails(connecting, typeof(TimeoutException));
            Check(!client.IsConnected, "Nonce timeout left a connection published");
        }
        Console.WriteLine("PASS connection deadline includes a stalled authentication nonce");

        await using (var peer = new Peer())
        using (var client = new SkpdbClient())
        {
            peer.GateHandshake = 1;
            var old = client.ConnectAsync(peer.Options);
            await peer.HandshakeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(peer.Options);
            await Fails(old, typeof(OperationCanceledException));
            peer.HandshakeProceed.TrySetResult();
            Check(await client.PingAsync() == "PONG", "Old handshake closed replacement connection");
        }
        Console.WriteLine("PASS latest connection wins; canceled old handshake cannot publish or close its replacement");

        await using (var peer = new Peer())
        using (var client = new SkpdbClient())
        {
            await client.ConnectAsync(peer.Options);
            peer.GateHandshake = 1;
            var independent = client.OpenIndependentChannelAsync();
            await peer.HandshakeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            client.Disconnect();
            await client.ConnectAsync(peer.Options);
            peer.HandshakeProceed.TrySetResult();
            await Fails(independent, typeof(OperationCanceledException));
            Check(await client.PingAsync() == "PONG", "Stale independent channel affected new parent session");
            var active = client.ExecAsync("stall");
            await peer.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var queuedDeadline = new CancellationTokenSource(3000);
            var queued = client.PingAsync(queuedDeadline.Token);
            await client.ConnectAsync(peer.Options);
            await Fails(active, typeof(IOException));
            await Fails(queued, typeof(OperationCanceledException));
            Check(await client.PingAsync() == "PONG", "Old request invalidated or ran on a new session");
        }
        Console.WriteLine("PASS independent channels and queued requests stay bound to their originating connection");

        await using (var peer = new Peer())
        {
            var client = new SkpdbClient();
            await client.ConnectAsync(peer.Options);
            var stalled = client.ExecAsync("stall");
            await peer.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Fails(stalled, typeof(TimeoutException));
            Check(!client.IsConnected, "Default request timeout kept a corrupt stream");
            await client.ConnectAsync(peer.Options);
            var inFlight = client.ExecAsync("stall");
            await Task.Delay(30);
            client.Dispose(); client.Dispose();
            try { await inFlight; } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            await Fails(client.PingAsync(), typeof(ObjectDisposedException));
        }
        Console.WriteLine("PASS default request deadline, in-flight disposal and repeated disposal");

        await using (var peer = new Peer())
        using (var client = new SkpdbClient())
        {
            await client.ConnectAsync(peer.Options);
            await Fails(client.ReadFileAsync("/truncated", 0, 100), typeof(InvalidDataException));
            await Fails(client.ReadFileAsync("/oversized", 0, 1), typeof(InvalidDataException));
            await Fails(client.ExecAsync("truncated"), typeof(InvalidDataException));
            await Fails(client.ListAsync("/truncated"), typeof(InvalidDataException));
            var path = Path.GetFullPath(".scratch/download-" + Guid.NewGuid().ToString("N") + ".bin");
            await File.WriteAllTextAsync(path, "previous valid file");
            await Fails(client.DownloadFileAsync("/truncated", path), typeof(InvalidDataException));
            Check(await File.ReadAllTextAsync(path) == "previous valid file", "Failed download overwrote existing file");
            await client.DownloadFileAsync("/valid", path);
            Check(await File.ReadAllTextAsync(path) == "ok", "Atomic download did not replace the file");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp").Any(),
                "Download temporary file leaked");
            Check(await client.PingAsync() == "PONG", "Complete malformed application body broke framing");
        }
        Console.WriteLine("PASS malformed/oversized file chunks rejected; failed download preserves existing file and successful download replaces it");

        await using (var peer = new Peer())
        using (var client = new SkpdbClient())
        {
            await client.ConnectAsync(peer.Options);
            var data = new byte[1_400_000]; new Random(2026).NextBytes(data);
            using var input = new MemoryStream(data);
            await client.UploadFileAsync(input, "/文件 test.bin");
            using var output = new MemoryStream();
            await client.DownloadFileAsync("/文件 test.bin", output);
            Check(output.ToArray().SequenceEqual(data), "Multi-chunk upload/download changed binary data");
            using var empty = new MemoryStream();
            await client.UploadFileAsync(empty, "/文件 test.bin");
            Check((await client.ReadFileAsync("/文件 test.bin", 0, 100)).Length == 0, "Empty upload did not truncate old remote file");
            using var partial = new MemoryStream(new byte[123]);
            await Fails(client.UploadFileAsync(partial, "/partial-write"), typeof(InvalidOperationException));
            Check(await client.PingAsync() == "PONG", "Partial write application failure corrupted framing");
        }
        Console.WriteLine("PASS 1.4MB binary upload/download roundtrip with Unicode path, empty-file truncation and partial-write rejection");
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = new();
        private readonly Task _accept;
        public int GateHandshake;
        private readonly ConcurrentDictionary<string, byte[]> _files = new();
        public TaskCompletionSource HandshakeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HandshakeProceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConnectionOptions Options { get; }
        public Peer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Options = new("127.0.0.1", port, port == 65535 ? port - 1 : port + 1, "local-lifecycle", 1);
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
                if (Interlocked.Exchange(ref GateHandshake, 0) != 0)
                { HandshakeEntered.TrySetResult(); await HandshakeProceed.Task.WaitAsync(_stop.Token); }
                var stream = client.GetStream();
                var nonce = RandomNumberGenerator.GetBytes(32);
                await stream.WriteAsync(nonce, _stop.Token);
                var proof = new byte[32]; await stream.ReadExactlyAsync(proof, _stop.Token);
                if (!CryptographicOperations.FixedTimeEquals(proof, HMACSHA256.HashData(Encoding.UTF8.GetBytes(Options.Key), nonce))) return;
                while (!_stop.IsCancellationRequested)
                {
                    var header = new byte[4]; await stream.ReadExactlyAsync(header, _stop.Token);
                    var frame = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
                    await stream.ReadExactlyAsync(frame, _stop.Token);
                    var op = frame[0];
                    if (op == 'E' && Encoding.UTF8.GetString(frame[1..]) == "stall")
                    { RequestEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, _stop.Token); }
                    byte[] body = [];
                    if (op == 'K') body = "PONG"u8.ToArray();
                    if (op == 'E')
                    { body = new byte[8]; BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4), 10); }
                    if (op == 'L')
                    { body = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(body, 10); }
                    if (op == 'W')
                    {
                        var offset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(1)));
                        var end = Array.IndexOf(frame, (byte)0, 10);
                        var path = Encoding.UTF8.GetString(frame.AsSpan(10, end - 10));
                        var data = frame[(end + 1)..];
                        var previous = frame[9] == 1 ? Array.Empty<byte>() : _files.GetValueOrDefault(path) ?? Array.Empty<byte>();
                        var file = new byte[Math.Max(previous.Length, offset + data.Length)];
                        previous.CopyTo(file, 0); data.CopyTo(file, offset); _files[path] = file;
                        body = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(body, path == "/partial-write" ? data.Length - 1 : data.Length);
                    }
                    if (op == 'R')
                    {
                        var path = Encoding.UTF8.GetString(frame[13..]);
                        if (_files.TryGetValue(path, out var file))
                        {
                            var offset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(1)));
                            var amount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(9)));
                            var count = Math.Min(amount, Math.Max(0, file.Length - offset));
                            body = new byte[4 + count]; BinaryPrimitives.WriteInt32BigEndian(body, count);
                            if (count > 0) file.AsSpan(offset, count).CopyTo(body.AsSpan(4));
                        }
                        else
                        {
                            body = new byte[6];
                            BinaryPrimitives.WriteInt32BigEndian(body, path == "/truncated" ? 10 : 2);
                            "ok"u8.CopyTo(body.AsSpan(4));
                        }
                    }
                    var response = new byte[] { op, 0 }.Concat(body).ToArray();
                    BinaryPrimitives.WriteInt32BigEndian(header, response.Length);
                    await stream.WriteAsync(header, _stop.Token); await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_clients); _stop.Dispose(); }
    }
}
