using SkpdbDesktop;
internal static class GyroDeviceTests
{
    public static async Task RunAsync(string host,string key)
    {
        using var client=new SkpdbClient();await client.ConnectAsync(new(host,Key:key));
        var info=await client.GetInfoAsync();
        var device=TouchDeviceInfo.Parse(await client.TouchInfoAsync());
        var display=TouchDeviceInfo.ParseDisplay((await client.ExecAsync("dumpsys display")).Output);
        if(display is { } d)device=device.WithDisplay(d.Rotation,d.Width,d.Height);
        var profile=GamepadProfileStore.CreateDefaultProfile("com.android.shell");profile.Move.Enabled=false;profile.Buttons.Clear();
        await using var engine=new GamepadEngine();engine.ConfigureFastPath(host,info.FastPort,info.FastToken,info.FastProtocol,info.FastMaxRecords);
        try
        {
            await engine.StartAsync(client,device,profile);
            await engine.SetGyroModeAsync(true);engine.SetViewMode(true);
            for(int i=0;i<40;i++){engine.AddMouseDelta(1,2);await Task.Delay(20);}
            await Task.Delay(1200);
            if(!engine.IsRunning||!engine.GyroMode||!engine.GyroEventsConfirmed)throw new InvalidOperationException("Real client gyro stream/health failed: "+engine.Metrics.LastError);
            await engine.SetGyroModeAsync(false);
            if(engine.GyroMode)throw new InvalidOperationException("Real client disable failed");
            await engine.StopAsync();
            if(await client.PingAsync()!="PONG")throw new InvalidOperationException("Parent connection lost");
            Console.WriteLine("GYRO_DEVICE_PASS real global HAL install ACK, SKG2, two axes, confirmed sensor events and reliable disarm");
        }
        finally
        {
            await engine.StopAsync();
            // Restore the owned test hook through the native client's existing protocol.
            await client.GyroRemoveAsync();
        }
    }
}
