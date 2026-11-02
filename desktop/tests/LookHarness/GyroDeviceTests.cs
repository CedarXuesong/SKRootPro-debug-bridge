using SkpdbDesktop;
internal static class GyroDeviceTests
{
    public static async Task RunAsync(string host,string key)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var client=new SkpdbClient();await client.ConnectAsync(new(host,Key:key));
        var info=await client.GetInfoAsync();
        var device=TouchDeviceInfo.Parse(await client.TouchInfoAsync());
        var display=TouchDeviceInfo.ParseDisplay((await client.ExecAsync("dumpsys display")).Output);
        if(display is { } d)device=device.WithDisplay(d.Rotation,d.Width,d.Height);
        var profile=GamepadProfileStore.CreateDefaultProfile("com.android.shell");profile.Move.Enabled=false;profile.Buttons.Clear();
        await using var engine=new GamepadEngine();engine.ConfigureFastPath(host,info.FastPort,info.FastToken,info.FastProtocol,info.FastMaxRecords);
        engine.Log += (_, text) => Console.WriteLine("ENGINE " + text);
        try
        {
            await engine.StartAsync(client,device,profile);
            await engine.SetGyroModeAsync(true);engine.SetViewMode(true);
            for(int i=0;i<40;i++){engine.AddMouseDelta(1,2);await Task.Delay(20);}
            for (var wait = 0; wait < 20 && engine.GyroMode && !engine.GyroEventsConfirmed; wait++)
                await Task.Delay(100);
            if(!engine.IsRunning||!engine.GyroMode||!engine.GyroEventsConfirmed)throw new InvalidOperationException($"Real client gyro stream/health failed: running={engine.IsRunning} gyro={engine.GyroMode} confirmed={engine.GyroEventsConfirmed} error={engine.Metrics.LastError}");
            await engine.SetGyroModeAsync(false);
            if(engine.GyroMode)throw new InvalidOperationException("Real client disable failed");
            await engine.StopAsync();
            if(await client.PingAsync()!="PONG")throw new InvalidOperationException("Parent connection lost");
            for (var cycle = 0; cycle < 40; cycle++)
            {
                await engine.StartAsync(client, device, profile);
                await engine.SetGyroModeAsync(true);
                engine.SetViewMode(true);
                for (var sample = 0; sample < 6; sample++)
                {
                    engine.AddMouseDelta(.25, -.5);
                    await Task.Delay(20);
                }
                if (!engine.IsRunning || !engine.GyroMode)
                    throw new InvalidOperationException($"Real restart {cycle + 1} failed: " + engine.Metrics.LastError);
                if (cycle % 2 == 0) await engine.SetGyroModeAsync(false);
                // Exercise both explicit gyro disable and Stop while gyro is active.
                await engine.StopAsync();
                if (engine.IsRunning || engine.GyroMode || await client.PingAsync() != "PONG")
                    throw new InvalidOperationException($"Restart {cycle + 1} left active input or lost the parent connection");
            }
            await engine.StartAsync(client, device, profile);
            await engine.SetGyroModeAsync(true);
            engine.SetViewMode(true);
            for (var sample = 0; sample < 60; sample++)
            {
                engine.AddMouseDelta(.25, .5);
                await Task.Delay(20);
            }
            for (var wait = 0; wait < 20 && engine.GyroMode && !engine.GyroEventsConfirmed; wait++)
                await Task.Delay(100);
            if (!engine.IsRunning || !engine.GyroMode || !engine.GyroEventsConfirmed)
                throw new InvalidOperationException("Sensor events did not resume after 40 restarts: " + engine.Metrics.LastError);
            Console.WriteLine("GYRO_DEVICE_PASS real global HAL install ACK, SKG2, two axes, confirmed sensor events and reliable disarm");
            Console.WriteLine("GYRO_RESTART_DEVICE_PASS 40 engine/gyro restarts, explicit and implicit disarm, parent PING and resumed sensor events");
        }
        finally
        {
            await engine.StopAsync();
            // Restore the owned test hook through the native client's existing protocol.
            await client.GyroRemoveAsync();
        }
    }
}
