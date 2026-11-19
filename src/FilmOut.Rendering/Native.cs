using System.Runtime.InteropServices;
using System.Text;

namespace FilmOut.Rendering;

internal static class Native
{
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint WM_CLOSE = 0x0010;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_INPUT = 0x00FF;
    public const int MA_NOACTIVATE = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const uint PM_REMOVE = 0x0001;

    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const ushort RI_KEY_BREAK = 1;

    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;
    public const uint ES_CONTINUOUS = 0x80000000;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    public static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObjectEx(IntPtr handle, uint milliseconds, bool alertable);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

/// <summary>
/// The handful of DXGI calls that timing depends on, made directly through the COM vtable so
/// they don't depend on how a wrapper library chooses to name them. Indices follow the
/// interface declarations in dxgi.h, dxgi1_2.h and dxgi1_3.h.
/// </summary>
internal static unsafe class DxgiTiming
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FrameStatistics
    {
        public uint PresentCount;
        public uint PresentRefreshCount;
        public uint SyncRefreshCount;
        public long SyncQPCTime;
        public long SyncGPUTime;
    }

    // IUnknown (3) + IDXGIObject (4) + IDXGIDeviceSubObject (1) = 8, then IDXGISwapChain:
    // Present 8, GetBuffer 9, SetFullscreenState 10, GetFullscreenState 11, GetDesc 12,
    // ResizeBuffers 13, ResizeTarget 14, GetContainingOutput 15, GetFrameStatistics 16, GetLastPresentCount 17.
    // IDXGISwapChain1 adds 18–28. IDXGISwapChain2: SetSourceSize 29, GetSourceSize 30,
    // SetMaximumFrameLatency 31, GetMaximumFrameLatency 32, GetFrameLatencyWaitableObject 33.
    private const int GetFrameStatisticsSlot = 16;
    private const int GetLastPresentCountSlot = 17;
    private const int SetMaximumFrameLatencySlot = 31;
    private const int GetFrameLatencyWaitableObjectSlot = 33;

    private static void** VTable(IntPtr comObject) => *(void***)comObject;

    public static bool TryGetFrameStatistics(IntPtr swapChain, out FrameStatistics stats)
    {
        FrameStatistics s;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, FrameStatistics*, int>)VTable(swapChain)[GetFrameStatisticsSlot];
        int hr = fn(swapChain, &s);
        stats = s;
        return hr >= 0;
    }

    public static uint GetLastPresentCount(IntPtr swapChain)
    {
        uint count;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)VTable(swapChain)[GetLastPresentCountSlot];
        return fn(swapChain, &count) >= 0 ? count : 0;
    }

    public static void SetMaximumFrameLatency(IntPtr swapChain2, uint latency)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)VTable(swapChain2)[SetMaximumFrameLatencySlot];
        Marshal.ThrowExceptionForHR(fn(swapChain2, latency));
    }

    public static IntPtr GetFrameLatencyWaitableObject(IntPtr swapChain2)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)VTable(swapChain2)[GetFrameLatencyWaitableObjectSlot];
        return fn(swapChain2);
    }
}

/// <summary>Compiles HLSL with the system's d3dcompiler_47.dll (present on Windows 10 and later).</summary>
internal static unsafe class ShaderCompiler
{
    private const uint D3DCOMPILE_ENABLE_STRICTNESS = 1u << 11;
    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1u << 15;

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte* srcData, nuint srcDataSize, string? sourceName, IntPtr defines,
        IntPtr include, string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    public static byte[] Compile(string source, string entryPoint, string target)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        IntPtr code, errors;
        int hr;
        fixed (byte* p = bytes)
            hr = D3DCompile(p, (nuint)bytes.Length, "Pipeline.hlsl", IntPtr.Zero, IntPtr.Zero, entryPoint, target,
                D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, out code, out errors);
        try
        {
            if (hr < 0)
            {
                string detail = errors != IntPtr.Zero
                    ? Encoding.UTF8.GetString(BlobBytes(errors)).TrimEnd('\0')
                    : $"HRESULT 0x{hr:X8}";
                throw new InvalidOperationException($"The display shader ({entryPoint}) failed to compile: {detail}");
            }
            return BlobBytes(code);
        }
        finally
        {
            Release(code);
            Release(errors);
        }
    }

    // ID3DBlob: IUnknown (0–2), GetBufferPointer 3, GetBufferSize 4.
    private static byte[] BlobBytes(IntPtr blob)
    {
        void** vt = *(void***)blob;
        var getPointer = (delegate* unmanaged[Stdcall]<IntPtr, void*>)vt[3];
        var getSize = (delegate* unmanaged[Stdcall]<IntPtr, nuint>)vt[4];
        var result = new byte[(int)getSize(blob)];
        new ReadOnlySpan<byte>(getPointer(blob), result.Length).CopyTo(result);
        return result;
    }

    private static void Release(IntPtr unknown)
    {
        if (unknown == IntPtr.Zero) return;
        void** vt = *(void***)unknown;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vt[2])(unknown);
    }
}
