namespace SkpdbDesktop;

/// <summary>Retains short presses between samples and fences held inputs across mode changes.
/// The owner supplies synchronization; one transition per input is consumed per sample.</summary>
public sealed class SampledInputState
{
    private sealed class State
    {
        public bool Physical, Sampled, Suppressed;
        public Queue<bool> Edges { get; } = new();
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _canonicalKeys;
    public SampledInputState(bool canonicalKeys = true) => _canonicalKeys = canonicalKeys;

    public static string Canonical(string key)
    {
        var text = key.Trim();
        if (text.Equals("Shift", StringComparison.OrdinalIgnoreCase)) return "LeftShift";
        if (text.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || text.Equals("Control", StringComparison.OrdinalIgnoreCase)) return "LeftControl";
        if (text.Equals("Alt", StringComparison.OrdinalIgnoreCase) || text.Equals("Menu", StringComparison.OrdinalIgnoreCase)) return "LeftAlt";
        return text.Equals("Esc", StringComparison.OrdinalIgnoreCase) ? "Escape" : text;
    }

    public static bool IsMouse(string key)
    {
        var text = key.Trim();
        return text.Equals("MouseLeft", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("MouseRight", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("MouseMiddle", StringComparison.OrdinalIgnoreCase);
    }

    private string Name(string key) => _canonicalKeys ? Canonical(key) : key;

    public void Set(string key, bool down)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        key = Name(key);
        if (!_states.TryGetValue(key, out var state)) _states[key] = state = new();
        if (state.Physical == down) return; // Includes OS key repeat.
        state.Physical = down;
        if (state.Suppressed)
        {
            if (!down) state.Suppressed = false;
            return;
        }
        // A stalled transport must not turn an arbitrarily large backlog into
        // minutes of delayed actions. Overflow cancels this gesture until UP.
        if (state.Edges.Count >= 64)
        {
            state.Edges.Clear(); state.Sampled = false; state.Suppressed = down;
            return;
        }
        state.Edges.Enqueue(down);
    }

    public void Advance()
    {
        foreach (var state in _states.Values)
            state.Sampled = !state.Suppressed &&
                (state.Edges.TryDequeue(out var next) ? next : state.Physical);
    }

    public bool Pressed(string key) => _states.TryGetValue(Name(key), out var state) && state.Sampled;

    public void Cancel(string key)
    {
        if (!_states.TryGetValue(Name(key), out var state)) return;
        state.Edges.Clear(); state.Sampled = false; state.Suppressed = state.Physical;
    }

    public void Cancel(bool mouseOnly = false)
    {
        foreach (var (key, state) in _states)
        {
            if (mouseOnly && !IsMouse(key)) continue;
            state.Edges.Clear(); state.Sampled = false; state.Suppressed = state.Physical;
        }
    }

    public void Reset() => _states.Clear();
}
