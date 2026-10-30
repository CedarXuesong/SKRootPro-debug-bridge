using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using SkpdbDesktop;

internal static class WireTests
{
    public static async Task Run(SkpdbClient client, ModuleInfo module, TouchDeviceInfo device,
        HttpClient http, string host)
    {
        if (module.FastProtocol < 2) throw new InvalidOperationException("SKF2 module required");
        var stream = (uint)Random.Shared.Next(1, int.MaxValue);
        uint generation = 0;
        var token = Convert.FromHexString(module.FastToken);
        using var udp = new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Parse(host), module.FastPort);
        var x = (int)(device.DisplayWidth * .75);
        var y = (int)(device.DisplayHeight * .52);
        var dpr = (await http.GetFromJsonAsync<JsonElement>("/state")).GetProperty("dpr").GetDouble();

        TouchBatchEvent At(byte action, int px) { var p = device.ToNative(px, y); return new(action, p.X, p.Y, 1); }
        Task Control(params TouchBatchEvent[] batch) => client.TouchStreamBatchAsync(stream, ++generation, batch);
        async Task Move(uint gen, uint sequence, int px)
        {
            var p = At(2, px);
            var data = new byte[45];
            "SKF2"u8.CopyTo(data); token.CopyTo(data, 4);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), stream);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), gen);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(28), sequence);
            data[32] = 1; data[33] = 2;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(35), p.Slot);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(37), (uint)p.X);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(41), (uint)p.Y);
            await udp.SendAsync(data, endpoint);
        }
        async Task Check(double expected, string label, bool released = false)
        {
            await Task.Delay(180);
            var state = await http.GetFromJsonAsync<JsonElement>("/state");
            var actual = state.GetProperty("dxTotal").GetDouble();
            if (Math.Abs(actual - expected) > 1.5 || released && state.GetProperty("n").GetInt32() != 0)
                throw new InvalidOperationException($"{label}: got {state.GetRawText()}, expected dx={expected:F2}");
            Console.WriteLine("PASS " + label);
        }
        async Task CheckStale(long before, int count, string label)
        {
            await Task.Delay(120);
            if ((await client.GetInfoAsync()).FastStale - before < count)
                throw new InvalidOperationException(label + ": stale packets were not rejected");
            Console.WriteLine("PASS " + label);
        }
        try
        {
            await Control(At(1, x));
            await Move(1, 2, x + 50);
            await Task.Delay(50);
            var stale = (await client.GetInfoAsync()).FastStale;
            await Move(1, 1, x - 100);
            await Move(1, 2, x - 150);
            await CheckStale(stale, 2, "out-of-order and duplicate packets rejected");
            await Control(new TouchBatchEvent(3, 0, 0, 1));
            // Check exact displacement after UP: Android can extrapolate a
            // held pointer beyond its last raw sample until the next event.
            await Check(50 / dpr, "sequenced move accepted", released: true);
            await Control(At(1, x));
            stale = (await client.GetInfoAsync()).FastStale;
            await Move(1, 999, x - 100);
            await CheckStale(stale, 1, "late packet cannot move a reused slot");
            await Move(3, 1, x + 50);
            await Task.Delay(80);
            try
            {
                await client.TouchStreamBatchAsync(stream, 2, new[] { At(2, x - 100) });
                throw new InvalidOperationException("stale reliable generation was accepted");
            }
            catch (InvalidOperationException e) when (e.Message.Contains("stale touch generation")) { }
            await Control(new TouchBatchEvent(3, 0, 0, 1));
            await Check(100 / dpr, "new generation accepts sequence restart", released: true);
            stale = (await client.GetInfoAsync()).FastStale;
            await Move(3, 2000, x + 200);
            await CheckStale(stale, 1, "late packet after release rejected");
            await Check(100 / dpr, "release rejects late packets and leaves zero touches", released: true);
            Console.WriteLine("All device transport regression scenarios passed.");
        }
        finally { await Control(new TouchBatchEvent(3, 0, 0, 1)); }
    }
}
