using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SkpdbDesktop;

internal static class KnockRetryTests
{
    public static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var knock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // No TCP listener until the second fresh UDP burst.
        listener = new TcpListener(IPAddress.Loopback, port);
        var token = deadline.Token;
        var knocks = 0;
        var nonces = new HashSet<ulong>();
        var peer = Task.Run(async () =>
        {
            while (nonces.Count < 2)
            {
                var bytes = (await knock.ReceiveAsync(token)).Buffer;
                if (bytes.Length != 60 || !bytes.AsSpan(0, 8).SequenceEqual("SKPDBK01"u8))
                    throw new InvalidDataException("Invalid knock framing");
                var mac = HMACSHA256.HashData("retry-test"u8, bytes.AsSpan(0, 28));
                if (!CryptographicOperations.FixedTimeEquals(mac, bytes.AsSpan(28)))
                    throw new InvalidDataException("Invalid signed knock");
                ++knocks;
                nonces.Add(BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(20, 8)));
            }
            listener.Start();
            using var tcp = await listener.AcceptTcpClientAsync(token);
            using var stream = tcp.GetStream();
            var nonce = RandomNumberGenerator.GetBytes(32);
            await stream.WriteAsync(nonce, token);
            var proof = new byte[32]; await stream.ReadExactlyAsync(proof, token);
            if (!proof.AsSpan().SequenceEqual(HMACSHA256.HashData("retry-test"u8, nonce)))
                throw new InvalidDataException("Invalid authentication proof");
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
            var body = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            await stream.ReadExactlyAsync(body, token);
            if (!body.AsSpan().SequenceEqual("K"u8)) throw new InvalidDataException("Expected PING");
            BinaryPrimitives.WriteInt32BigEndian(header, 6);
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(new byte[] { (byte)'K', 0, 80, 79, 78, 71 }, token);
        }, token);
        try
        {
            using var client = new SkpdbClient();
            await client.ConnectAsync(new("127.0.0.1", port,
                ((IPEndPoint)knock.Client.LocalEndPoint!).Port, "retry-test", 6), token);
            await peer;
            if (!client.IsConnected || knocks < 4 || nonces.Count < 2)
                throw new InvalidOperationException("Initial UDP loss was not recovered");
            Console.WriteLine("PASS lost initial UDP burst recovers with a fresh signed knock and authenticated PONG");
        }
        finally { deadline.Cancel(); listener.Stop(); try { await peer; } catch when (token.IsCancellationRequested) { } }
    }
}
