namespace SkpdbDesktop;

public sealed record GyroSession(uint Stream, string Writer, string SensorTypes, bool DirectChannel,
    uint InitialHits = 0);

public sealed class GyroEventHealth(uint initialHits)
{
    public const double TimeoutSeconds = 2;
    private uint _hits = initialHits;
    private double _lastProgress;
    public bool HasEvents { get; private set; }

    public bool Observe(uint hits, double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (hits != _hits)
        {
            _hits = hits;
            _lastProgress = elapsedSeconds;
            HasEvents = true;
        }
        return elapsedSeconds - _lastProgress < TimeoutSeconds;
    }
}

/// <summary>Maps angular increments to device-space rates, preserving both axes and rotation.</summary>
public sealed class GyroMotion
{
    private double _x, _y;
    public void Reset() => _x = _y = 0;
    public (double X,double Y,double Z) Step(double dx,double dy,double seconds,double sensitivity,
        int rotation,bool invertX,bool invertY,double smoothing)
    {
        if(!double.IsFinite(seconds)||seconds<=0||!double.IsFinite(dx)||!double.IsFinite(dy))return (0,0,0);
        var alpha=1-Math.Pow(Math.Clamp(smoothing,0,.95),seconds*240);
        _x+=(Math.Clamp(dx*sensitivity*(invertX?-1:1)/seconds,-15,15)-_x)*alpha;
        _y+=(Math.Clamp(dy*sensitivity*(invertY?-1:1)/seconds,-15,15)-_y)*alpha;
        // Landscape screen axes rotate with the logical display; device axes do not.
        // The native gyro convention reverses both axes independently of user toggles.
        var pitch=_y;var yaw=_x;
        return (rotation switch {1=>-yaw,2=>-pitch,3=>yaw,_=>pitch},
            rotation switch {1=>pitch,2=>-yaw,3=>-pitch,_=>yaw},0);
    }
}
