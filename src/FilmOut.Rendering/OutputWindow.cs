using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FilmOut.Rendering;

/// <summary>
/// A borderless window covering the output display. It never takes focus, hides the cursor,
/// and receives raw keyboard input even while the control window has focus, so trigger keys
/// and foot pedals work whatever the operator last clicked. Lives on the render thread.
/// </summary>
internal sealed unsafe class OutputWindow : IDisposable
{
    private const string ClassName = "FilmOutOutputWindow";
    private const int ErrorClassAlreadyExists = 1410;

    private static readonly Native.WndProc s_windowProc = StaticWindowProc;
    private static bool s_classRegistered;

    [ThreadStatic] private static OutputWindow? t_current;

    private readonly HashSet<int> _keysDown = [];

    public OutputWindow(int x, int y, int width, int height, bool topmost)
    {
        IntPtr instance = Native.GetModuleHandleW(null);
        if (!s_classRegistered)
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_windowProc),
                hInstance = instance,
                lpszClassName = ClassName,
            };
            if (Native.RegisterClassExW(ref wc) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorClassAlreadyExists) throw new Win32Exception(error);
            }
            s_classRegistered = true;
        }

        t_current = this;
        int exStyle = Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | (topmost ? Native.WS_EX_TOPMOST : 0);
        Handle = Native.CreateWindowExW(exStyle, ClassName, "FilmOut output", Native.WS_POPUP,
            x, y, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        var keyboard = new[]
        {
            new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = Handle },
        };
        if (!Native.RegisterRawInputDevices(keyboard, 1, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't register for keyboard input.");

        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
    }

    public IntPtr Handle { get; }

    /// <summary>Raised once per key press (auto-repeat is ignored), with the Windows virtual-key code.</summary>
    public event Action<int>? KeyPressed;

    public void PumpMessages()
    {
        while (Native.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
    }

    public void Dispose()
    {
        Native.DestroyWindow(Handle);
        if (ReferenceEquals(t_current, this)) t_current = null;
    }

    private static IntPtr StaticWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var window = t_current;
        if (window is not null)
        {
            switch (msg)
            {
                case Native.WM_SETCURSOR:
                    Native.SetCursor(IntPtr.Zero);
                    return 1;
                case Native.WM_MOUSEACTIVATE:
                    return Native.MA_NOACTIVATE;
                case Native.WM_CLOSE:
                    return IntPtr.Zero; // only the engine closes this window
                case Native.WM_INPUT:
                    window.OnRawInput(lParam);
                    break; // DefWindowProc must still run for WM_INPUT
            }
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnRawInput(IntPtr rawInput)
    {
        uint headerSize = (uint)sizeof(Native.RAWINPUTHEADER);
        uint size = 0;
        Native.GetRawInputData(rawInput, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0 || size > 256) return;

        byte* buffer = stackalloc byte[(int)size];
        if (Native.GetRawInputData(rawInput, Native.RID_INPUT, (IntPtr)buffer, ref size, headerSize) == uint.MaxValue) return;

        var header = (Native.RAWINPUTHEADER*)buffer;
        if (header->dwType != Native.RIM_TYPEKEYBOARD) return;
        var keyboard = (Native.RAWKEYBOARD*)(buffer + headerSize);

        int vk = keyboard->VKey;
        if (vk is 0 or 0xFF) return; // part of an escape sequence, not a real key
        bool released = (keyboard->Flags & Native.RI_KEY_BREAK) != 0;
        if (released) _keysDown.Remove(vk);
        else if (_keysDown.Add(vk)) KeyPressed?.Invoke(vk);
    }
}
