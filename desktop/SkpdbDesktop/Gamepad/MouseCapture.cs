using System.Runtime.InteropServices;

namespace SkpdbDesktop;

/// <summary>
/// Clips the Windows cursor to the phone-screen rectangle while controlling.
/// Mouse mode keeps the arrow visible inside that rectangle; view mode hides
/// the arrow, pins it to the rectangle center and lets Raw Input drive the
/// relative look motion.
/// </summary>
public sealed class MouseCapture
{
    private const int MaxCursorHides = 8;

    private sealed record CaptureState(IntPtr Window, PointerCaptureRegion Region, bool Hidden);
    private CaptureState? _state;
    private int _hidden;
    private IntPtr _arrowCursor;
    public bool IsActive => Volatile.Read(ref _state) is not null;
    public bool IsHidden => Volatile.Read(ref _state) is { Hidden: true };

    /// <summary>
    /// Current Windows cursor position normalized inside the clipped phone
    /// rectangle. Mouse-mode drags poll this so they do not depend on WinUI
    /// PointerMoved events continuing to arrive after pointer capture.
    /// </summary>
    public bool TryGetNormalizedCursor(out double x, out double y)
    {
        x = 0;
        y = 0;
        var state = Volatile.Read(ref _state);
        if (state is null || state.Hidden)
            return false;
        if (!GetPhysicalCursorPos(out var point))
            return false;
        return state.Region.TryNormalize(point.X, point.Y, out x, out y);
    }

    // Hook coordinates belong to the button event itself. Reading GetCursorPos
    // later can return the next gesture's position when the UI is busy.
    public bool TryNormalizeScreenPoint(int screenX, int screenY, out double x, out double y)
    {
        x = y = 0;
        var state = Volatile.Read(ref _state);
        return state is { Hidden: false } &&
            state.Region.TryNormalize(screenX, screenY, out x, out y);
    }

    public void Apply(IntPtr hwnd, double leftDip, double topDip,
        double widthDip, double heightDip, double rasterizationScale, bool hide)
    {
        if (hwnd == IntPtr.Zero || widthDip <= 0 || heightDip <= 0)
            return;
        if (!GetClientRect(hwnd, out _))
            return;
        var origin = new POINT();
        if (!ClientToScreen(hwnd, ref origin))
            return;

        var scale = rasterizationScale;
        if (scale <= 0)
            scale = 1;

        var rect = new RECT
        {
            Left = origin.X + (int)Math.Round(leftDip * scale),
            Top = origin.Y + (int)Math.Round(topDip * scale),
            Right = origin.X + (int)Math.Round((leftDip + widthDip) * scale),
            Bottom = origin.Y + (int)Math.Round((topDip + heightDip) * scale)
        };
        if (rect.Right - rect.Left < 16 || rect.Bottom - rect.Top < 16)
            return;

        if (!ClipCursor(ref rect))
            return;
        Volatile.Write(ref _state, new CaptureState(hwnd,
            new PointerCaptureRegion(rect.Left, rect.Top, rect.Right, rect.Bottom), hide));
        EnsureCapture();

        if (hide)
        {
            HideSystemCursor();
            CenterCursor();
            KeepHidden();
        }
        else
        {
            RestoreSystemCursor();
            if (_arrowCursor == IntPtr.Zero)
                _arrowCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512);
            SetCursor(_arrowCursor);
        }
    }

    /// <summary>
    /// Re-pins the hidden cursor to the phone-screen center. Raw Input keeps
    /// reporting relative motion, so the physical mouse can travel forever.
    /// </summary>
    public void KeepHidden()
    {
        if (Volatile.Read(ref _state) is not { Hidden: true })
            return;
        CenterCursor();
        var blank = EnsureBlankCursor();
        if (blank != IntPtr.Zero)
            SetCursor(blank);
    }

    public void Release()
    {
        var state = Interlocked.Exchange(ref _state, null);
        // Clear our clip and any stale clip left by an earlier process.
        ClipCursor(IntPtr.Zero);
        if (state is not null)
        {
            if (GetCapture() == state.Window)
                ReleaseCapture();
            if (_arrowCursor == IntPtr.Zero)
                _arrowCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512);
            if (_arrowCursor != IntPtr.Zero)
                SetCursor(_arrowCursor);
        }
        RestoreSystemCursor();
    }

    private void CenterCursor()
    {
        if (Volatile.Read(ref _state) is not { Hidden: true } state)
            return;
        var x = (state.Region.Left + state.Region.Right) / 2;
        var y = (state.Region.Top + state.Region.Bottom) / 2;
        SetCursorPos(x, y);
    }

    // Called on the UI thread in both modes. Button-up never releases capture.
    public void EnsureCapture()
    {
        if (Volatile.Read(ref _state) is { } state && GetCapture() != state.Window)
            SetCapture(state.Window);
    }

    private void HideSystemCursor()
    {
        if (_hidden > 0)
            return;
        for (var i = 0; i < MaxCursorHides; i++)
        {
            _hidden++;
            if (ShowCursor(false) < 0)
                break;
        }
    }

    private void RestoreSystemCursor()
    {
        while (_hidden > 0)
        {
            ShowCursor(true);
            _hidden--;
        }
    }

    private static IntPtr _blankCursorShared;

    private static IntPtr EnsureBlankCursor()
    {
        if (_blankCursorShared != IntPtr.Zero)
            return _blankCursorShared;
        // A 1x1 monochrome cursor row is padded to a WORD.
        var andMask = new byte[] { 0xFF, 0x00 };
        var xorMask = new byte[] { 0x00, 0x00 };
        _blankCursorShared = CreateCursor(IntPtr.Zero, 0, 0, 1, 1, andMask, xorMask);
        return _blankCursorShared;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr rect);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateCursor(IntPtr instance, int xHotSpot, int yHotSpot,
        int width, int height, byte[] andPlane, byte[] xorPlane);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetPhysicalCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

}
