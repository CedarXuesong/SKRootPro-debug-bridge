using System.Runtime.InteropServices;

namespace SkpdbDesktop;

/// <summary>
/// Receives relative mouse motion through Raw Input on a hidden window.
///
/// Pointer events stop at the canvas edge and low-level hooks stop at the
/// desktop edge. Both make the camera stop turning while the player keeps
/// moving the mouse. Raw Input reports deltas, so the view can keep turning as
/// long as the mouse moves, which is what the gamepad page needs for its
/// look mode. If registration fails the page falls back to pointer deltas.
/// </summary>
public sealed class RawMouseInput : IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const int RimTypeMouse = 0;
    private const ushort MouseMoveAbsolute = 0x0001;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string? ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public int Type;
        public int Size;
        public IntPtr Device;
        public IntPtr Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        public ushort ButtonFlags;
        public ushort ButtonData;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    private readonly WindowProc _proc;
    private readonly string _className = "SkpdbRawMouse_" + Guid.NewGuid().ToString("N");
    private readonly IntPtr _instance;
    private IntPtr _window;

    public event Action<int, int>? Moved;

    public bool IsRegistered { get; private set; }

    private RawMouseInput()
    {
        _proc = OnMessage;
        _instance = GetModuleHandle(null);
    }

    public static RawMouseInput? TryCreate()
    {
        var input = new RawMouseInput();
        return input.Initialize() ? input : null;
    }

    private bool Initialize()
    {
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProc = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = _instance,
            ClassName = _className
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            return false;
        }

        _window = CreateWindowEx(
            WsExToolWindow | WsExNoActivate, _className, "SkpdbRawMouse", WsPopup,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            UnregisterClass(_className, _instance);
            return false;
        }

        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x02,
                Flags = RidevInputSink,
                Target = _window
            }
        };
        IsRegistered = RegisterRawInputDevices(devices, 1,
            (uint)Marshal.SizeOf<RawInputDevice>());
        return IsRegistered;
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmInput && IsRegistered)
        {
            ReadMouse(lParam);
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ReadMouse(IntPtr rawInput)
    {
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        if (GetRawInputData(rawInput, RidInput, IntPtr.Zero, ref size, headerSize) != 0 ||
            size < headerSize)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInput, RidInput, buffer, ref size, headerSize) != size)
            {
                return;
            }
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RimTypeMouse)
            {
                return;
            }
            var mouse = Marshal.PtrToStructure<RawMouse>(
                IntPtr.Add(buffer, Marshal.SizeOf<RawInputHeader>()));
            if ((mouse.Flags & MouseMoveAbsolute) != 0)
            {
                return;
            }
            if (mouse.LastX == 0 && mouse.LastY == 0)
            {
                return;
            }
            Moved?.Invoke(mouse.LastX, mouse.LastY);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (IsRegistered)
        {
            try
            {
                var devices = new[]
                {
                    new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RidevRemove }
                };
                RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>());
            }
            catch
            {
            }
            IsRegistered = false;
        }
        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
            UnregisterClass(_className, _instance);
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className,
        string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices,
        uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command,
        IntPtr data, ref uint size, uint headerSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
