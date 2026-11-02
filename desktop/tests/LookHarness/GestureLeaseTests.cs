using SkpdbDesktop;
internal static class GestureLeaseTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    public static async Task RunAsync()
    {
        await using var peer = new SnapshotTests.Peer();
        using var client = new SkpdbClient(); await client.ConnectAsync(peer.Options);
        Check((await client.GetInfoAsync()).GestureLeaseSupported, "Capability was not parsed");
        var session = await GestureLeaseSession.StartAsync(client);
        Check(session.Changed && peer.GestureEnabled == 1, "Start did not acquire lease");
        await Task.Delay(1700);
        Check(peer.GestureRequests.Count(p => p == 1) == 1 && peer.GestureRequests.Contains((byte)2),
            "Heartbeat reacquires rather than renews the owned lease");
        var slow = client.ExecAsync("fixture slow");
        Check(peer.GestureEnabled == 1 && session.Failure is null, "Primary request disturbed independent lease");
        await slow; await session.DisposeAsync(); await session.DisposeAsync();
        var stopped = peer.GestureRequests.Count; await Task.Delay(1600);
        Check(peer.GestureEnabled == 0 && peer.GestureRequests.Count == stopped && await client.PingAsync() == "PONG",
            "Stop left a lease, wrote late heartbeat or disposed primary");
        session = await GestureLeaseSession.StartAsync(client);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Failed += _ => failed.TrySetResult();
        await client.ConnectAsync(peer.Options);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Check(session.Failure is not null, "Stale lease followed a replaced primary connection");
        await session.DisposeAsync(); Check(peer.GestureEnabled == 0, "Failure cleanup left gesture suspended");
        peer.GestureSupported = false;
        Check(!(await client.GetInfoAsync()).GestureLeaseSupported, "Old module incorrectly advertised support");
        try { await GestureLeaseSession.StartAsync(client); throw new Exception("Missing ACK was accepted"); }
        catch (InvalidOperationException) { }
        Check(await client.PingAsync() == "PONG" && peer.GestureEnabled == 0, "Rejected start damaged primary state");
        Console.WriteLine("PASS gesture coordination: capability, wire ACK, independent renewals, stop, repeated dispose, primary replacement and missing capability/ACK");
    }
}
