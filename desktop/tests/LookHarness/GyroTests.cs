using SkpdbDesktop;
internal static class GyroTests
{
    private static void Check(bool x,string m){if(!x)throw new InvalidOperationException(m);}
    public static async Task RunAsync()
    {
        var health = new GyroEventHealth(40);
        Check(health.Observe(40, .5) && !health.HasEvents, "ACK alone confirmed sensor delivery");
        Check(!health.Observe(40, 2), "No-event session remained enabled");
        health = new GyroEventHealth(40);
        Check(health.Observe(41, .5) && health.HasEvents, "Actual target events not confirmed");
        Check(health.Observe(41, 2.49) && !health.Observe(41, 2.5), "Stalled events did not expire");
        health = new GyroEventHealth(uint.MaxValue);
        Check(health.Observe(0, .5) && health.HasEvents, "Counter wrap lost sensor progress");
        foreach(var rotation in Enumerable.Range(0,4))
        foreach(var invertX in new[]{false,true})
        foreach(var invertY in new[]{false,true})
        {
            var motion=new GyroMotion();
            var horizontal=motion.Step(2,0,1.0/240,.001,rotation,invertX,invertY,0);
            motion.Reset();
            var vertical=motion.Step(0,2,1.0/240,.001,rotation,invertX,invertY,0);
            var expectedHorizontal=rotation switch
            {
                1=>(-.48,0.0),2=>(0.0,-.48),3=>(.48,0.0),_=>(0.0,.48)
            };
            var expectedVertical=rotation switch
            {
                1=>(0.0,.48),2=>(-.48,0.0),3=>(0.0,-.48),_=>(.48,0.0)
            };
            Check(Math.Abs(horizontal.X-expectedHorizontal.Item1*(invertX?-1:1))<1e-8 &&
                Math.Abs(horizontal.Y-expectedHorizontal.Item2*(invertX?-1:1))<1e-8 &&
                horizontal.Z==0,"Horizontal gyro sign/rotation or independent X inversion changed");
            Check(Math.Abs(vertical.X-expectedVertical.Item1*(invertY?-1:1))<1e-8 &&
                Math.Abs(vertical.Y-expectedVertical.Item2*(invertY?-1:1))<1e-8 &&
                vertical.Z==0,"Vertical gyro sign/rotation or independent Y inversion changed");
            Check(Math.Abs(horizontal.X*vertical.X+horizontal.Y*vertical.Y)<1e-8,"Gyro screen axes overlap");
        }
        foreach(var hz in new[]{60,120,240,500})
        {
            var m=new GyroMotion();double angle=0;
            for(var i=0;i<hz;i++){var r=m.Step(100.0/hz,0,1.0/hz,.001,0,false,false,0);angle+=r.Y/hz;}
            Check(Math.Abs(angle-.1)<1e-8,"Sampling-rate dependent gyro gain or default sign changed");
        }
        var profile=new GamepadProfile{GyroEnabled=true};
        Check(profile.Clone().GyroEnabled,"Cloning lost gyro preference");
        Check(System.Text.Json.JsonSerializer.Serialize(profile).Contains("GyroEnabled"),"Gyro preference omitted");
        Check(!profile.Look.InvertX&&!profile.Look.InvertY,"Default gyro signs changed toggle defaults");
        foreach(var invertY in new[]{false,true})
        {
            var legacy=GamepadProfileStore.DeserializeProfile(
                "{\"look\":{\"invertY\":"+invertY.ToString().ToLowerInvariant()+"},"+
                "\"extraLooks\":[{\"invertY\":"+(!invertY).ToString().ToLowerInvariant()+"}]}")!;
            Check(!legacy.Look.InvertX&&legacy.Look.InvertY==invertY &&
                !legacy.ExtraLooks[0].InvertX&&legacy.ExtraLooks[0].InvertY==!invertY,
                "Loading a legacy profile changed existing inversion preferences");
        }
        foreach(var invertX in new[]{false,true})
        foreach(var invertY in new[]{false,true})
        {
            profile.Look.InvertX=invertX;profile.Look.InvertY=invertY;
            profile.ExtraLooks=[new GamepadLookSettings{InvertX=!invertX,InvertY=!invertY}];
            var saved=GamepadProfileStore.DeserializeProfile(GamepadProfileStore.SerializeProfile(profile.Clone()))!;
            saved.Validate();
            Check(saved.Look.InvertX==invertX&&saved.Look.InvertY==invertY &&
                saved.ExtraLooks[0].InvertX==!invertX&&saved.ExtraLooks[0].InvertY==!invertY,
                "Cloning, validation or persistence changed per-component inversion preferences");
        }
        // A missing/unsupported gyro endpoint must not change a running engine's input path.
        await using var peer=new ReconfigurationTests.Peer();using var client=new SkpdbClient();await client.ConnectAsync(peer.Options);
        await using var engine=new GamepadEngine();engine.ConfigureFastPath("127.0.0.1",39997,new string('0',32),2,16);
        var p=GamepadProfileStore.CreateDefaultProfile("com.test.gyro");var device=new TouchDeviceInfo("gyro-test","","",1280,2772,9,true,true);
        await engine.StartAsync(client,device,p);
        var scope = "device-hal";
        peer.GyroResponse = frame =>
        {
            if (frame[1] == 1)
                Check(frame.Length == 2, "Global installation still sent an application UID");
            return $"installed=1\nprotocol=2\nenabled=1\nstream=7\nhits=12\nscope={scope}\nlayer=vendor-hal\nwriter=fixture\nsensor_types=4,16,11,15\n";
        };
        var session = await client.GyroInstallAsync();
        Check(session.Stream == 7 && session.InitialHits == 12, "Global HAL handshake lost confirmed events");
        scope = "uid-private-copy";
        try { await client.GyroInstallAsync(); throw new Exception("Legacy UID backend accepted as global"); }
        catch (InvalidOperationException) { }
        scope = "device-hal";
        await engine.SetGyroModeAsync(true);
        var other = p.Clone(); other.Package = "com.test.other";
        await engine.ReconfigureAsync(device, other);
        Check(engine.GyroMode, "Global HAL mode remained tied to an application switch");
        await engine.SetGyroModeAsync(false);
        for (var i = 0; i < 40; i++)
        {
            await engine.StopAsync();
            await engine.StartAsync(client, device, p);
            await engine.SetGyroModeAsync(true);
            await engine.SetGyroModeAsync(false);
            Check(engine.IsRunning && !engine.GyroMode, "Repeated gyro start/stop lost touch mode");
        }
        await Task.Delay(100);
        Check(peer.CurrentConnections == 2 && peer.PeakConnections <= 5,
            $"Repeated starts leaked connections: current={peer.CurrentConnections}, peak={peer.PeakConnections}");
        Console.WriteLine("PASS 40 rapid gyro/engine restarts keep at most five transient TCP sessions and retire old monitors");
        var queryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.GyroResponseAsync = async frame =>
        {
            if (frame[1] == 0) { queryEntered.TrySetResult(); await queryProceed.Task; }
            return peer.GyroResponse!(frame);
        };
        await engine.SetGyroModeAsync(true);
        await queryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { queryProceed.TrySetResult(); }
        await engine.StartAsync(client, device, p);
        await engine.SetGyroModeAsync(true);
        await Task.Delay(100);
        Check(engine.GyroMode && engine.IsRunning, "A late old health response disabled the new stream");
        await engine.SetGyroModeAsync(false);
        peer.GyroResponseAsync = null;
        Console.WriteLine("PASS in-flight health cancellation and late old response preserve the next gyro start and parent connection");
        peer.GyroResponse = null;
        try{await engine.SetGyroModeAsync(true);throw new InvalidOperationException("Unsupported gyro unexpectedly enabled");}
        catch(InvalidOperationException error) when(!error.Message.Contains("unexpectedly")){}
        Check(engine.IsRunning&&!engine.GyroMode,"Failed gyro handshake destroyed touch mode");await engine.StopAsync();
        Check(await client.PingAsync()=="PONG","Gyro failure closed parent");
        Console.WriteLine("PASS global HAL handshake without UID, app switching, event health, reversed default axes, independent X/Y inversion in four rotations, legacy preferences and failed-handshake touch preservation");
    }
}
