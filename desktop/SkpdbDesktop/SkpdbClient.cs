using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SkpdbDesktop;

public readonly record struct TouchBatchEvent(byte Action, int X, int Y, ushort Slot);

/// <summary>
/// The wire client. It mirrors skpdb_probe/src/common/channel.cpp and
/// host/src/protocol.cpp: a signed UDP knock, a raw TCP authentication
/// challenge, then length-prefixed request/response frames.
/// </summary>
public sealed class SkpdbClient : IDisposable
{
    private const int MaxFrame = 1024 * 1024;
    private const byte OpExec = (byte)'E';
    private const byte OpRead = (byte)'R';
    private const byte OpWrite = (byte)'W';
    private const byte OpList = (byte)'L';
    private const byte OpPing = (byte)'K';
    private const byte OpMemRead = (byte)'M';
    private const byte OpMemMaps = (byte)'A';
    private const byte OpMemInfo = (byte)'G';
    private const byte OpMemWrite = (byte)'X';
    private const byte OpTouch = (byte)'I';
    private const byte OpTouchBatch = (byte)'J';
    private const byte OpCursor = (byte)'C';
    private const byte OpAdmin = (byte)'B';

    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _connecting;
    private bool _disposed;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private byte[] _key = Array.Empty<byte>();
    private int _timeoutSeconds = 10;
    private ConnectionOptions? _connection;
    private long _connectionGeneration;

    public bool IsConnected { get { lock (_stateGate) return _stream is not null; } }
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    public async Task ConnectAsync(ConnectionOptions options, CancellationToken cancellationToken = default)
    {
        var host = NormalizeHost(options.Host);
        var keyText = (options.Key ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("设备地址不能为空");
        if (keyText.Length == 0)
            throw new InvalidOperationException("Root 密钥不能为空");

        if (options.Port is < 1 or > 65535 || options.KnockPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "端口必须为 1..65535");
        var timeout = Math.Clamp(options.TimeoutSeconds, 1, 120);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(timeout));
        long generation;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DisconnectLocked();
            _connecting = lifetime;
            generation = _connectionGeneration;
        }
        var key = Encoding.UTF8.GetBytes(keyText);
        TcpClient? tcp = null;
        try
        {
            var token = lifetime.Token;
            IPAddress address;
            try { address = await ResolveAsync(host, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            { throw new InvalidOperationException("无法解析设备地址 " + host, error); }
            Exception? lastError = null;
            const int attempts = 6;
            var connectTimeout = TimeSpan.FromSeconds(Math.Min(1.0, Math.Max(0.4, timeout / 4.0)));
            for (var attempt = 0; attempt < attempts && tcp is null; attempt++)
            {
                // A Wi-Fi/RNDIS route can drop the initial UDP burst. Retry
                // with a fresh signed nonce instead of retrying only TCP
                // against a listener that never received its opening knock.
                await SendKnockAsync(address, options.KnockPort, key, token).ConfigureAwait(false);
                var candidate = new TcpClient(address.AddressFamily);
                try
                {
                    await candidate.ConnectAsync(address, options.Port, token).AsTask()
                        .WaitAsync(connectTimeout, token).ConfigureAwait(false);
                    candidate.NoDelay = true;
                    tcp = candidate;
                }
                catch (Exception error)
                {
                    candidate.Dispose();
                    token.ThrowIfCancellationRequested();
                    lastError = error;
                    await Task.Delay(130, token).ConfigureAwait(false);
                }
            }
            if (tcp is null)
                throw new InvalidOperationException($"设备未响应 knock：{host}:{options.Port} 在 {attempts} 次尝试后仍未开放。", lastError);
            var stream = tcp.GetStream();
            try
            {
                await AuthenticateAsync(stream, key, token).ConfigureAwait(false);
                var pong = await ExchangeAsync(stream, OpPing, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
                if (!pong.AsSpan().SequenceEqual("PONG"u8))
                    throw new InvalidDataException("设备返回了意外的认证响应");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new InvalidOperationException("密钥校验失败：设备拒绝了 HMAC 认证，请检查 Root 密钥。", error);
            }
            lock (_stateGate)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != _connectionGeneration || !ReferenceEquals(_connecting, lifetime))
                    throw new OperationCanceledException("连接已被替换", token);
                _tcp = tcp; _stream = stream; _key = key;
                _timeoutSeconds = timeout;
                _connection = options with { Host = address.ToString(), Key = "", TimeoutSeconds = timeout };
                key = Array.Empty<byte>(); tcp = null; // Ownership transfers only after authentication.
                _connecting = null;
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_stateGate)
                if (generation != _connectionGeneration) throw;
            throw new TimeoutException($"连接或认证超过 {timeout} 秒，请检查设备后重试", error);
        }
        finally
        {
            tcp?.Dispose();
            CryptographicOperations.ZeroMemory(key);
            lock (_stateGate)
                if (ReferenceEquals(_connecting, lifetime)) _connecting = null;
        }
    }

    /// <summary>
    /// Opens a separately authenticated socket to the same device. Input must
    /// not wait behind shell commands or file transfers on the control socket.
    /// The returned client belongs to its caller and must be disposed.
    /// </summary>
    public async Task<SkpdbClient> OpenIndependentChannelAsync(
        CancellationToken cancellationToken = default)
    {
        ConnectionOptions connection;
        long generation;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is null || _connection is null || _key.Length == 0)
                throw new InvalidOperationException("设备连接已断开，无法建立独立触摸通道");
            connection = _connection with { Key = Encoding.UTF8.GetString(_key) };
            generation = _connectionGeneration;
        }
        var channel = new SkpdbClient();
        try
        {
            await channel.ConnectAsync(connection, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
                if (_disposed || _stream is null || generation != _connectionGeneration)
                    throw new OperationCanceledException("建立独立通道期间设备连接已变化", cancellationToken);
            return channel;
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Accepts the formats people paste into the host box: a bare address, a
    /// URL with a scheme and path, or an IPv6 literal in brackets. The port is
    /// parsed separately by the caller.
    /// </summary>
    public static string NormalizeHost(string host)
    {
        var text = (host ?? string.Empty).Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            text = text[(scheme + 3)..];
        var slash = text.IndexOfAny(['/', '\\']);
        if (slash >= 0)
            text = text[..slash];
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            var end = text.IndexOf(']');
            if (end > 0)
                return text[1..end];
        }
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon)
            text = text[..colon];
        return text;
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("设备地址没有可用的 IP");
    }

    public void Disconnect()
    {
        lock (_stateGate) DisconnectLocked();
    }

    private void DisconnectLocked()
    {
        Interlocked.Increment(ref _connectionGeneration);
        _connecting?.Cancel();
        _connecting = null;
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
        _connection = null;
        CryptographicOperations.ZeroMemory(_key);
        _key = Array.Empty<byte>();
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            DisconnectLocked();
        }
        // Do not dispose the managed semaphore while an in-flight request or
        // queued waiter can still reach its finally/Release. No WaitHandle is used.
    }

    private static async Task SendKnockAsync(IPAddress address, int knockPort, byte[] key,
        CancellationToken cancellationToken)
    {
        var packet = new byte[60];
        Encoding.ASCII.GetBytes("SKPDBK01", packet.AsSpan(0, 8));
        packet[8] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(12, 8),
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20, 8),
            (ulong)RandomNumberGenerator.GetInt32(int.MaxValue));
        var mac = HMACSHA256.HashData(key, packet.AsSpan(0, 28));
        mac.CopyTo(packet.AsSpan(28, 32));

        // Knocking the resolved address (rather than the host name) keeps the
        // UDP datagram on the same address family as the TCP connect; a name
        // that resolves to IPv6 first would otherwise knock an address the
        // module never listens on.
        using var udp = new UdpClient(address.AddressFamily);
        var target = new IPEndPoint(address, knockPort);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await udp.SendAsync(packet, target, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException error)
            {
                throw new InvalidOperationException("发送 knock 失败: " + error.Message, error);
            }
        }
    }

    private static async Task AuthenticateAsync(NetworkStream stream, byte[] key,
        CancellationToken cancellationToken)
    {
        var nonce = new byte[32];
        await ReadExactAsync(stream, nonce, cancellationToken).ConfigureAwait(false);
        var proof = HMACSHA256.HashData(key, nonce);
        await stream.WriteAsync(proof, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> RequestAsync(byte opcode, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        NetworkStream stream;
        int timeout;
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            stream = _stream ?? throw new InvalidOperationException("尚未连接设备");
            timeout = _timeoutSeconds;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!cancellationToken.CanBeCanceled) deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        var entered = false;
        try
        {
            await _io.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            lock (_stateGate)
                if (!ReferenceEquals(stream, _stream)) throw new OperationCanceledException("请求所属连接已变化");
            return await ExchangeAsync(stream, opcode, body, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error)
        {
            if (entered) DisconnectStream(stream);
            if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"设备请求超过 {timeout} 秒", error);
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SocketException or ObjectDisposedException)
        {
            if (entered) DisconnectStream(stream);
            throw;
        }
        finally { if (entered) _io.Release(); }
    }

    private void DisconnectStream(NetworkStream stream)
    {
        lock (_stateGate)
            if (ReferenceEquals(stream, _stream)) DisconnectLocked();
    }

    private static async Task<byte[]> ExchangeAsync(NetworkStream stream, byte opcode, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var frame = new byte[1 + body.Length];
        frame[0] = opcode;
        body.Span.CopyTo(frame.AsSpan(1));

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)frame.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length < 2 || length > MaxFrame)
            throw new InvalidDataException("设备返回了无效帧");

        var response = new byte[length];
        await ReadExactAsync(stream, response, cancellationToken).ConfigureAwait(false);
        if (response[0] != opcode)
            throw new InvalidDataException("设备返回了意外的响应");

        if (response[1] != 0)
        {
            var errorLength = response.Length >= 6
                ? (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(2, 4)),
                    (uint)(response.Length - 6))
                : 0;
            var message = errorLength > 0
                ? Encoding.UTF8.GetString(response, 6, errorLength)
                : "设备请求失败";
            throw new InvalidOperationException(message);
        }

        return response[2..];
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new IOException("连接已关闭");
            offset += read;
        }
    }

    public async Task<ModuleInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(OpAdmin, new byte[] { 1 }, cancellationToken)
            .ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(payload);
        var values = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
                values[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
        }

        string Get(string key) => values.TryGetValue(key, out var value) ? value : string.Empty;
        int GetInt(string key) => int.TryParse(Get(key), out var value) ? value : 0;
        long GetLong(string key) => long.TryParse(Get(key), out var value) ? value : 0;

        return new ModuleInfo(
            Get("name"), Get("version"), Get("id32"), GetInt("pid"), GetLong("uptime_seconds"),
            GetInt("service_port"), GetInt("knock_port"), GetInt("sessions"), GetInt("peers"),
            GetInt("sessions_total"), Get("memory_backends"), Get("listening") == "yes",
            GetInt("fast_port"), Get("fast_token"), Math.Max(1, GetInt("fast_protocol")),
            GetLong("fast_stale"), Get("admin_socket"),
            GetInt("fast_max_records") is > 0 and <= 16 ? GetInt("fast_max_records") : 8,
            GetLong("fast_rx"), GetLong("fast_ok"), GetLong("fast_dropped"), Get("fast_realtime") == "1",
            Get("build_channel"), Get("debug_overlay") != "0", Get("gesture_lease") == "1");
    }

    public async Task<string> PingAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(OpPing, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    public async Task<ExecResult> ExecAsync(string command, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(OpExec, Encoding.UTF8.GetBytes(command), cancellationToken)
            .ConfigureAwait(false);
        if (payload.Length < 8)
            throw new InvalidOperationException("设备返回的终端响应不完整");
        var code = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
        var size = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4, 4));
        if (size != payload.Length - 8) throw new InvalidDataException("设备返回的终端输出长度不匹配");
        return new ExecResult((int)code, Encoding.UTF8.GetString(payload, 8, (int)size));
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string path,
        CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(OpList, Encoding.UTF8.GetBytes(path), cancellationToken)
            .ConfigureAwait(false);
        if (payload.Length < 4)
            throw new InvalidOperationException("设备返回的目录列表不完整");
        var size = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
        if (size != payload.Length - 4) throw new InvalidDataException("设备返回的目录长度不匹配");
        var listing = Encoding.UTF8.GetString(payload, 4, (int)size);
        var entries = new List<FileEntry>();
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length >= 2 && line[1] == '\t')
                entries.Add(new FileEntry(line[0], line[2..].TrimEnd('\r')));
        }
        return entries;
    }

    public async Task<byte[]> ReadFileAsync(string path, long offset, uint amount,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || amount > MaxFrame - 6) throw new ArgumentOutOfRangeException(nameof(amount));
        var body = new byte[12 + Encoding.UTF8.GetByteCount(path)];
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), (ulong)offset);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), amount);
        Encoding.UTF8.GetBytes(path, body.AsSpan(12));
        var payload = await RequestAsync(OpRead, body, cancellationToken).ConfigureAwait(false);
        if (payload.Length < 4)
            throw new InvalidOperationException("设备返回的读取响应不完整");
        var size = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
        if (size != payload.Length - 4 || size > amount)
            throw new InvalidDataException("设备返回的文件块长度无效，下载未完成");
        return payload[4..];
    }

    public async Task<long> WriteFileAsync(string path, long offset, byte[] data, bool truncate,
        CancellationToken cancellationToken = default)
    {
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var body = new byte[10 + pathBytes.Length + data.Length];
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(0, 8), (ulong)offset);
        body[8] = truncate ? (byte)1 : (byte)0;
        pathBytes.CopyTo(body.AsSpan(9));
        body[9 + pathBytes.Length] = 0;
        data.CopyTo(body.AsSpan(10 + pathBytes.Length));
        var payload = await RequestAsync(OpWrite, body, cancellationToken).ConfigureAwait(false);
        if (payload.Length < 4)
            throw new InvalidOperationException("设备返回的写入响应不完整");
        return BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
    }

    public async Task DownloadFileAsync(string remote, Stream output,
        CancellationToken cancellationToken = default)
    {
        long offset = 0;
        const uint chunk = 900 * 1024;
        while (true)
        {
            var data = await ReadFileAsync(remote, offset, chunk, cancellationToken)
                .ConfigureAwait(false);
            if (data.Length == 0)
                break;
            await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            offset += data.Length;
            if (data.Length < chunk)
                break;
        }
    }

    public async Task DownloadFileAsync(string remote, string local,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(local);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await DownloadFileAsync(remote, file, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task UploadFileAsync(Stream input, string remote,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[512 * 1024 - 32];
        long offset = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            var written = await WriteFileAsync(remote, offset, buffer[..read], offset == 0,
                cancellationToken).ConfigureAwait(false);
            if (written != read)
                throw new InvalidOperationException("设备未完整写入数据");
            offset += read;
        }
        if (offset == 0)
            await WriteFileAsync(remote, 0, Array.Empty<byte>(), true, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task UploadFileAsync(string local, string remote,
        CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read);
        await UploadFileAsync(file, remote, cancellationToken).ConfigureAwait(false);
    }

    public Task<string> MemoryInfoAsync(CancellationToken cancellationToken = default) =>
        MemoryTextAsync(OpMemInfo, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public Task<string> MemoryMapsAsync(int pid, CancellationToken cancellationToken = default)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(body, (uint)pid);
        return MemoryTextAsync(OpMemMaps, body, cancellationToken);
    }

    private async Task<string> MemoryTextAsync(byte opcode, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var payload = await RequestAsync(opcode, body, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    public Task<string> TouchInfoAsync(CancellationToken cancellationToken = default) =>
        TouchAsync(0, 0, 0, 0, 0, 0, 9, cancellationToken);

    public Task<string> TouchDownAsync(int x, int y, int slot,
        CancellationToken cancellationToken = default) =>
        TouchAsync(1, x, y, 0, 0, 0, (ushort)slot, cancellationToken);

    public Task<string> TouchMoveAsync(int x, int y, int slot,
        CancellationToken cancellationToken = default) =>
        TouchAsync(2, x, y, 0, 0, 0, (ushort)slot, cancellationToken);

    public Task<string> TouchUpAsync(int slot, CancellationToken cancellationToken = default) =>
        TouchAsync(3, 0, 0, 0, 0, 0, (ushort)slot, cancellationToken);

    public Task<string> TouchTapAsync(int x, int y, int durationMs, int slot,
        CancellationToken cancellationToken = default) =>
        TouchAsync(4, x, y, 0, 0, (uint)durationMs, (ushort)slot, cancellationToken);

    public Task<string> TouchSwipeAsync(int x1, int y1, int x2, int y2, int durationMs, int slot,
        CancellationToken cancellationToken = default) =>
        TouchAsync(5, x1, y1, x2, y2, (uint)durationMs, (ushort)slot, cancellationToken);

    public async Task<string> TouchBatchAsync(IReadOnlyList<TouchBatchEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count is 0 or > 32)
        {
            throw new InvalidOperationException("触摸批次必须包含 1..32 个事件");
        }
        var body = new byte[1 + events.Count * 12];
        body[0] = (byte)events.Count;
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            var offset = 1 + i * 12;
            body[offset] = item.Action;
            body[offset + 1] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset + 2, 2), item.Slot);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(offset + 4, 4),
                (uint)Math.Max(0, item.X));
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(offset + 8, 4),
                (uint)Math.Max(0, item.Y));
        }
        var payload = await RequestAsync(OpTouchBatch, body, cancellationToken)
            .ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    // B/5 changes the touch generation atomically with the reliable batch.
    // SKF2 moves from any earlier generation are then discarded by the phone.
    public async Task<string> TouchStreamBatchAsync(uint stream, uint generation,
        IReadOnlyList<TouchBatchEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count is 0 or > 32 || stream == 0 || generation == 0)
            throw new InvalidOperationException("触摸流参数无效");
        var body = new byte[10 + events.Count * 12];
        body[0] = 5;
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1, 4), stream);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(5, 4), generation);
        body[9] = (byte)events.Count;
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            var offset = 10 + i * 12;
            body[offset] = item.Action;
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(offset + 2, 2), item.Slot);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(offset + 4, 4), (uint)Math.Max(0, item.X));
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(offset + 8, 4), (uint)Math.Max(0, item.Y));
        }
        var payload = await RequestAsync(OpAdmin, body, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    public async Task<bool> SetGestureLeaseAsync(bool enabled, CancellationToken cancellationToken = default, bool renew = false)
    {
        var payload = await RequestAsync(OpAdmin, new byte[] { 7, enabled ? (renew ? (byte)2 : (byte)1) : (byte)0 }, cancellationToken)
            .ConfigureAwait(false);
        var fields = Encoding.UTF8.GetString(payload).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);
        if (fields.GetValueOrDefault("gesture_lease") != (enabled ? "1" : "0") ||
            fields.GetValueOrDefault("gesture_changed") is not ("0" or "1"))
            throw new InvalidOperationException("模块未确认系统手势协调，请更新配套模块");
        return fields["gesture_changed"] == "1";
    }

    public async Task<string> CursorProbeAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(OpCursor, new byte[] { 0 }, cancellationToken)
            .ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    public async Task<GyroSession> GyroInstallAsync(CancellationToken cancellationToken = default)
    {
        var body=new byte[]{1};
        var result=Encoding.UTF8.GetString(await RequestAsync((byte)'Z',body,cancellationToken).ConfigureAwait(false));
        var fields=result.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(row=>row.Split('=',2))
            .Where(pair=>pair.Length==2).ToDictionary(pair=>pair[0],pair=>pair[1]);
        if(fields.GetValueOrDefault("installed")!="1"||fields.GetValueOrDefault("protocol")!="2"||
            fields.GetValueOrDefault("enabled")!="1"||!uint.TryParse(fields.GetValueOrDefault("stream"),out var stream)||stream==0||
            fields.GetValueOrDefault("scope")!="device-hal" || fields.GetValueOrDefault("layer")!="vendor-hal" ||
            !uint.TryParse(fields.GetValueOrDefault("hits"), out var hits))
            throw new InvalidOperationException("模块未确认整机 HAL 陀螺仪和数据流，请更新配套模块");
        return new(stream,fields.GetValueOrDefault("writer", "unknown"),fields.GetValueOrDefault("sensor_types", ""),
            fields.GetValueOrDefault("direct_channel")=="supported", hits);
    }

    public async Task GyroDisarmAsync(CancellationToken cancellationToken = default)
        => _ = await RequestAsync((byte)'Z',new byte[]{8},cancellationToken).ConfigureAwait(false);
    public async Task GyroRemoveAsync(CancellationToken cancellationToken = default)
        => _ = await RequestAsync((byte)'Z',new byte[]{2},cancellationToken).ConfigureAwait(false);

    public async Task GyroDisarmAsync(uint expectedStream,CancellationToken cancellationToken=default)
    {
        var body=new byte[5];body[0]=8;BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1),expectedStream);
        _=await RequestAsync((byte)'Z',body,cancellationToken).ConfigureAwait(false);
    }

    public async Task<uint> CheckGyroAsync(uint stream,CancellationToken cancellationToken=default)
    {
        var text=Encoding.UTF8.GetString(await RequestAsync((byte)'Z',new byte[]{0},cancellationToken).ConfigureAwait(false));
        var fields=text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(r=>r.Split('=',2)).Where(p=>p.Length==2).ToDictionary(p=>p[0],p=>p[1]);
        if(fields.GetValueOrDefault("installed")!="1"||fields.GetValueOrDefault("enabled")!="1"||
            fields.GetValueOrDefault("scope")!="device-hal"||fields.GetValueOrDefault("layer")!="vendor-hal"||
            fields.GetValueOrDefault("stream")!=stream.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidOperationException("陀螺仪写入未保持确认，或目标进程/数据流已改变");
        if (!uint.TryParse(fields.GetValueOrDefault("hits"), out var hits))
            throw new InvalidOperationException("模块缺少传感器事件计数，无法验证陀螺仪是否实际生效");
        return hits;
    }

    public async Task<int> ResolvePackageUidAsync(string package, CancellationToken cancellationToken = default)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(package,@"\A[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+\z"))
            throw new InvalidOperationException("请先选择有效应用配置，再启用陀螺仪");
        var result=await ExecAsync("cmd package list packages -U "+package,cancellationToken).ConfigureAwait(false);
        foreach(var row in result.Output.Split('\n'))
        {
            var match=System.Text.RegularExpressions.Regex.Match(row.Trim(),@"\Apackage:"+System.Text.RegularExpressions.Regex.Escape(package)+@"\s+uid:(\d+)\z");
            if(match.Success&&int.TryParse(match.Groups[1].Value,out var uid)&&uid>=1000)return uid;
        }
        throw new InvalidOperationException("未找到应用 UID："+package);
    }

    public async Task ControlOverlayAsync(ControlOverlayState state, bool enabled,
        CancellationToken cancellationToken = default)
    {
        byte[] payload;
        try { payload = await RequestAsync(OpCursor, state.Encode(enabled), cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException error) when (error.Message.Contains("unknown cursor action", StringComparison.Ordinal))
        { throw new InvalidOperationException("手机模块尚不支持调试显示层，请更新至 0.12.7 或更新版本", error); }
        if (!payload.AsSpan().SequenceEqual(enabled ? "hud=1"u8 : "hud=0"u8))
            throw new InvalidOperationException("模块未确认调试显示层，请更新手机模块至 0.12.7 或更新版本");
    }

    public async Task<string> CursorSetAsync(int x, int y, bool visible,
        CancellationToken cancellationToken = default)
    {
        var body = new byte[6];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1, 2), (ushort)Math.Clamp(x, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3, 2), (ushort)Math.Clamp(y, 0, 65535));
        body[5] = visible ? (byte)1 : (byte)0;
        var payload = await RequestAsync(OpCursor, body, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    public async Task<string> CursorDestroyAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(OpCursor, new byte[] { 2 }, cancellationToken)
            .ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    private async Task<string> TouchAsync(byte action, int x, int y, int x2, int y2,
        uint duration, ushort slot, CancellationToken cancellationToken)
    {
        var body = new byte[24];
        body[0] = action;
        body[1] = 0; // flags
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2, 2), slot);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), (uint)Math.Max(0, x));
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), (uint)Math.Max(0, y));
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12, 4), (uint)Math.Max(0, x2));
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), (uint)Math.Max(0, y2));
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(20, 4), duration);
        var payload = await RequestAsync(OpTouch, body, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    public async Task<long> MemoryReadAsync(int pid, ulong address, uint length,
        CancellationToken cancellationToken = default)
    {
        var body = new byte[17];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)pid);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(4, 8), address);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12, 4), length);
        body[16] = 0;
        var payload = await RequestAsync(OpMemRead, body, cancellationToken).ConfigureAwait(false);
        return payload.Length;
    }
}
