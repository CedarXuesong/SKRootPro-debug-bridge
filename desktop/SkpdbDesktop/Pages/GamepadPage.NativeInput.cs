namespace SkpdbDesktop;

public sealed partial class GamepadPage
{
    private static class NativeMouseHook
    {
        public delegate IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WhMouseLl = 14;
        private const int WmRButtonDown = 0x0204;
        private const int WmRButtonUp = 0x0205;
        private const int WmMButtonDown = 0x0207;
        private const int WmMButtonUp = 0x0208;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmMouseMove = 0x0200;
        private const int WmMouseWheel = 0x020A;
        private static MouseProc? _proc;
        private static Action<string, bool, int, int>? _onButton;
        private static Action? _onMove;
        private static Action<int>? _onWheel;
        private static IntPtr _hook;

        public static bool IsInstalled => _hook != IntPtr.Zero;

        public static void Install(Action<string, bool, int, int> onButton,
            Action onMove, Action<int> onWheel)
        {
            if (_hook != IntPtr.Zero)
            {
                return;
            }
            _onButton = onButton;
            _onMove = onMove;
            _onWheel = onWheel;
            _proc = (code, message, data) =>
            {
                if (code >= 0)
                {
                    var id = (int)message;
                    if (id == WmMouseMove)
                        _onMove?.Invoke();
                    else if (id == WmMouseWheel)
                    {
                        var mouse = System.Runtime.InteropServices.Marshal.PtrToStructure<HookPoint>(data);
                        _onWheel?.Invoke(unchecked((short)(mouse.MouseData >> 16)));
                    }
                    else
                    {
                        var button = id switch
                        {
                            WmLButtonDown or WmLButtonUp => "MouseLeft",
                            WmRButtonDown or WmRButtonUp => "MouseRight",
                            WmMButtonDown or WmMButtonUp => "MouseMiddle",
                            _ => null
                        };
                        if (button is not null)
                        {
                            var point = System.Runtime.InteropServices.Marshal.PtrToStructure<HookPoint>(data);
                            _onButton?.Invoke(button, id is WmLButtonDown or WmRButtonDown or WmMButtonDown,
                                point.X, point.Y);
                        }
                    }
                }
                return User32.CallNextHookEx(_hook, code, message, data);
            };
            _hook = User32.SetWindowsHookEx(WhMouseLl, _proc,
                User32.GetModuleHandle(null), 0);
        }

        public static void Uninstall()
        {
            if (_hook != IntPtr.Zero)
            {
                User32.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _proc = null;
            _onButton = null;
            _onMove = null;
            _onWheel = null;
        }

        // MSLLHOOKSTRUCT begins with POINT (two signed 32-bit screen pixels).
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct HookPoint
        {
            public int X;
            public int Y;
            public uint MouseData;
        }

        public static bool IsForegroundOwnedByCurrentProcess()
        {
            var foreground = NativeKeyboard.GetForegroundWindow();
            NativeKeyboard.GetWindowThreadProcessId(foreground, out var pid);
            return foreground != IntPtr.Zero && pid == Environment.ProcessId;
        }

        private static class User32
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int hook, MouseProc callback,
                IntPtr module, uint threadId);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool UnhookWindowsHookEx(IntPtr hook);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern IntPtr CallNextHookEx(IntPtr hook, int code,
                IntPtr message, IntPtr data);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            public static extern IntPtr GetModuleHandle(string? name);
        }
    }

    private static class NativeKeyboardHook
    {
        public delegate IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        private static Proc? _proc;
        private static Func<int, bool, bool>? _onKey;
        private static IntPtr _hook;

        public static void Install(Func<int, bool, bool> onKey)
        {
            if (_hook != IntPtr.Zero)
                return;
            _onKey = onKey;
            _proc = (code, message, data) =>
            {
                if (code >= 0)
                {
                    var id = (int)message;
                    if (id is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
                    {
                        var info = System.Runtime.InteropServices.Marshal
                            .PtrToStructure<KbdLlHookStruct>(data);
                        if (_onKey?.Invoke((int)info.VkCode, id is WmKeyDown or WmSysKeyDown) == true)
                            return (IntPtr)1;
                    }
                }
                return KbUser32.CallNextHookEx(_hook, code, message, data);
            };
            _hook = KbUser32.SetWindowsHookEx(WhKeyboardLl, _proc,
                KbUser32.GetModuleHandle(null), 0);
        }

        public static void Uninstall()
        {
            if (_hook != IntPtr.Zero)
            {
                KbUser32.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _proc = null;
            _onKey = null;
        }

        private static class KbUser32
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int hook, Proc callback,
                IntPtr module, uint threadId);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool UnhookWindowsHookEx(IntPtr hook);

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern IntPtr CallNextHookEx(IntPtr hook, int code,
                IntPtr message, IntPtr data);

            [System.Runtime.InteropServices.DllImport("kernel32.dll",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            public static extern IntPtr GetModuleHandle(string? name);
        }
    }

    private static class NativeKeyboard
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int key);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    }

}
