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
        foreach(var invert in new[]{false,true})
        {
            var motion=new GyroMotion();var horizontal=motion.Step(2,0,1.0/240,.001,rotation,invert,0);
            motion.Reset();var vertical=motion.Step(0,2,1.0/240,.001,rotation,invert,0);
            Check(Math.Abs(horizontal.X)+Math.Abs(horizontal.Y)>.4,"Horizontal gyro missing");
            Check(Math.Abs(vertical.X)+Math.Abs(vertical.Y)>.4,"Vertical gyro missing");
            Check(Math.Abs(horizontal.X*vertical.X+horizontal.Y*vertical.Y)<1e-8,"Gyro screen axes overlap");
        }
        foreach(var hz in new[]{60,120,240,500})
        {
            var m=new GyroMotion();double angle=0;
            for(var i=0;i<hz;i++){var r=m.Step(100.0/hz,0,1.0/hz,.001,0,false,0);angle+=r.Y/hz;}
            Check(Math.Abs(angle+.1)<1e-8,"Sampling-rate dependent gyro gain");
        }
        var profile=new GamepadProfile{GyroEnabled=true};
        Check(profile.Clone().GyroEnabled,"Cloning lost gyro preference");
        Check(System.Text.Json.JsonSerializer.Serialize(profile).Contains("GyroEnabled"),"Gyro preference omitted");
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
        peer.GyroResponse = null;
        try{await engine.SetGyroModeAsync(true);throw new InvalidOperationException("Unsupported gyro unexpectedly enabled");}
        catch(InvalidOperationException error) when(!error.Message.Contains("unexpectedly")){}
        Check(engine.IsRunning&&!engine.GyroMode,"Failed gyro handshake destroyed touch mode");await engine.StopAsync();
        Check(await client.PingAsync()=="PONG","Gyro failure closed parent");
        Console.WriteLine("PASS global HAL handshake without UID, app switching, event health, axes/rotations and failed-handshake touch preservation");
    }
}
