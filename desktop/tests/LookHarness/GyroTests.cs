using SkpdbDesktop;
internal static class GyroTests
{
    private static void Check(bool x,string m){if(!x)throw new InvalidOperationException(m);}
    public static async Task RunAsync()
    {
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
        try{await engine.SetGyroModeAsync(true);throw new InvalidOperationException("Unsupported gyro unexpectedly enabled");}
        catch(InvalidOperationException error) when(!error.Message.Contains("unexpectedly")){}
        Check(engine.IsRunning&&!engine.GyroMode,"Failed gyro handshake destroyed touch mode");await engine.StopAsync();
        Check(await client.PingAsync()=="PONG","Gyro failure closed parent");
        Console.WriteLine("PASS gyro two axes/four rotations/inversion, time-based gain, preferences and failed-handshake touch preservation");
    }
}
