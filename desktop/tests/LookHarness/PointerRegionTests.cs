using SkpdbDesktop;

internal static class PointerRegionTests
{
    public static void Run()
    {
        var region = new PointerCaptureRegion(1500, 400, 1900, 1200);
        Check(region.TryNormalize(1700, 800, out var x, out var y) && x == .5 && y == .5,
            "physical-pixel center");
        Check(region.TryNormalize(1500, 400, out x, out y) && x == 0 && y == 0, "top left");
        var last = (X: .5, Y: .5);
        // Simulate the old DPI-mismatched release coordinate. It must retain
        // the last position, never clamp to (1,1) and fling the touch right.
        if (region.TryNormalize(2850, 1800, out x, out y)) last = (x, y);
        Check(last == (.5, .5), "outside release replaced the endpoint");
        Check(!region.TryNormalize(1900, 800, out _, out _), "exclusive right edge");
        Check(!region.TryNormalize(1700, 1200, out _, out _), "exclusive bottom edge");
        Check(new PointerCaptureRegion(-1800, -800, -1400, 0).TryNormalize(-1600, -400,
            out x, out y) && x == .5 && y == .5, "negative monitor coordinates");
        Check(!new PointerCaptureRegion(1, 1, 1, 1).TryNormalize(1, 1, out _, out _), "empty region");
        Console.WriteLine("PASS 7 native coordinate cases, including outside-release endpoint retention");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
