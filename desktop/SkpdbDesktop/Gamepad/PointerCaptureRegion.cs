namespace SkpdbDesktop;

/// <summary>A single immutable physical-pixel coordinate space for native input.</summary>
public sealed record PointerCaptureRegion(int Left, int Top, int Right, int Bottom)
{
    public bool TryNormalize(int screenX, int screenY, out double x, out double y)
    {
        x = y = 0;
        // An out-of-region release is not a new endpoint at the nearest edge.
        // Keep the last valid sample instead of teleporting the touch there.
        if (Right <= Left || Bottom <= Top || screenX < Left || screenX >= Right ||
            screenY < Top || screenY >= Bottom)
            return false;
        x = (screenX - (double)Left) / (Right - (double)Left);
        y = (screenY - (double)Top) / (Bottom - (double)Top);
        return true;
    }
}
