using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using TV.Services;

namespace TV.Controls;

/// <summary>
/// Creates and caches a native WinUI 3 <see cref="InputCursor"/> backed by an in-memory
/// fully transparent Win32 HCURSOR via Windows App SDK's official <c>IInputCursorStaticsInterop</c>.
/// Assigning the returned cursor to <see cref="UIElement.ProtectedCursor"/> cleanly hides the
/// pointer over that XAML subtree without corrupting the process-wide Win32 <c>ShowCursor</c>
/// reference count or leaking across window / monitor boundaries.
/// </summary>
internal static class InputCursorHelper
{
    // Official IID from Windows App SDK header Microsoft.UI.Input.InputCursor.Interop.h:
    // DECLARE_INTERFACE_IID_(IInputCursorStaticsInterop, IInspectable, "ac6f5065-90c4-46ce-beb7-05e138e54117")
    private static readonly Guid InputCursorStaticsInteropIid = new("ac6f5065-90c4-46ce-beb7-05e138e54117");

    private const int SmCxCursor = 13;
    private const int SmCyCursor = 14;
    private const int IdcArrow = 32512;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateFromHCursorDelegate(nint thisPtr, nint hCursor, out nint result);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateCursor(
        nint hInst,
        int xHotSpot,
        int yHotSpot,
        int nWidth,
        int nHeight,
        byte[] pvAndPlane,
        byte[] pvXorPlane);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint hCursor);

    [DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private static readonly Action<UIElement, InputCursor?>? SetProtectedCursorDelegate = BuildProtectedCursorSetter();
    private static nint _blankHCursor;
    private static nint _arrowHCursor;
    private static InputCursor? _blankInputCursor;
    private static bool _initialized;

    private static Action<UIElement, InputCursor?>? BuildProtectedCursorSetter()
    {
        try
        {
            var setter = typeof(UIElement).GetProperty("ProtectedCursor", BindingFlags.Instance | BindingFlags.NonPublic)?.SetMethod;
            return setter is not null ? setter.CreateDelegate<Action<UIElement, InputCursor?>>() : null;
        }
        catch { return null; }
    }

    public static InputCursor? GetBlankCursor()
    {
        EnsureInitialized();
        return _blankInputCursor;
    }

    public static void SetElementCursor(UIElement? element, InputCursor? cursor)
    {
        if (element is null || SetProtectedCursorDelegate is null) return;
        try { SetProtectedCursorDelegate(element, cursor); }
        catch { }
    }

    /// <summary>
    /// Refreshes the OS cursor image. Only issues a 0-pixel <c>SetCursorPos</c> nudge when
    /// <paramref name="nudgeStationary"/> is true (hiding a stationary cursor or keyboard restore);
    /// NEVER calls <c>SetCursorPos</c> while the user is actively moving the mouse so cursor motion stays 100% fluid.
    /// </summary>
    public static void RefreshStationaryCursor(bool hide, bool nudgeStationary = true)
    {
        try
        {
            EnsureInitialized();
            if (hide)
            {
                if (_blankHCursor != 0) SetCursor(_blankHCursor);
            }
            else
            {
                if (_arrowHCursor == 0) _arrowHCursor = LoadCursorW(0, IdcArrow);
                if (_arrowHCursor != 0) SetCursor(_arrowHCursor);
            }
            if (nudgeStationary && GetCursorPos(out var pt))
                SetCursorPos(pt.X, pt.Y);
        }
        catch { }
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _arrowHCursor = LoadCursorW(0, IdcArrow);
            var w = Math.Clamp(GetSystemMetrics(SmCxCursor), 16, 128);
            var h = Math.Clamp(GetSystemMetrics(SmCyCursor), 16, 128);
            w = (w + 7) & ~7; // width must be a multiple of 8 for monochrome AND/XOR bitmasks

            _blankHCursor = CreateTransparentHCursor(w, h);
            if (_blankHCursor == 0 && (w != 32 || h != 32))
            {
                // Fallback to universal 32x32 monochrome cursor on exotic DPI / RDP environments
                w = h = 32;
                _blankHCursor = CreateTransparentHCursor(w, h);
            }
            if (_blankHCursor == 0)
            {
                Diagnostics.Log($"Blank cursor CreateCursor failed: Win32={Marshal.GetLastWin32Error()}");
                return;
            }

            using var factory = WinRT.ActivationFactory.Get("Microsoft.UI.Input.InputCursor", InputCursorStaticsInteropIid);
            var thisPtr = factory.ThisPtr;
            var vtbl = Marshal.ReadIntPtr(thisPtr);
            // IUnknown (3 slots) + IInspectable (3 slots) + CreateFromHCursor (slot 6)
            var fnPtr = Marshal.ReadIntPtr(vtbl, 6 * IntPtr.Size);
            var createFromHCursor = Marshal.GetDelegateForFunctionPointer<CreateFromHCursorDelegate>(fnPtr);
            var hr = createFromHCursor(thisPtr, _blankHCursor, out var cursorAbi);
            if (hr < 0 || cursorAbi == 0)
            {
                Diagnostics.Log($"InputCursor.CreateFromHCursor failed: HR=0x{hr:X8}");
                return;
            }
            try
            {
                _blankInputCursor = InputCursor.FromAbi(cursorAbi);
                Diagnostics.Log($"Blank InputCursor ready ({w}x{h})");
            }
            finally
            {
                Marshal.Release(cursorAbi);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Blank InputCursor init failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static nint CreateTransparentHCursor(int width, int height)
    {
        var planeLength = (width / 8) * height;
        var andPlane = new byte[planeLength];
        var xorPlane = new byte[planeLength];
        Array.Fill(andPlane, (byte)0xFF); // AND=1, XOR=0 => 100% transparent
        return CreateCursor(GetModuleHandleW(null), 0, 0, width, height, andPlane, xorPlane);
    }
}
