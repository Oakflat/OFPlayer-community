using System.Runtime.InteropServices;
using System.Text;
using OFPlayer.WinUI.Core.Input;

namespace OFPlayer.Community.Services;

/// <summary>
/// Bounds the initial native picker window without resetting its folder history.
/// Install on the UI thread immediately before Pick*Async and dispose after it.
/// </summary>
internal sealed class FilePickerWindowScope : IDisposable
{
    private const uint ObjectShow = 0x8002;
    private readonly nint owner;
    private readonly WinEventProc callback;
    private nint hook;
    private bool handled;

    public FilePickerWindowScope(nint owner)
    {
        this.owner = owner;
        callback = OnWindowShown;
        if (owner == 0) return;
        // The WinRT picker may be brokered. Observe show events, but accept only
        // the native dialog directly owned by the HWND passed to the picker.
        // OUTOFCONTEXT delivers callbacks on this UI thread's message loop.
        hook = SetWinEventHook(ObjectShow, ObjectShow, 0, callback, 0, 0, 0);
        if (hook == 0) System.Diagnostics.Trace.WriteLine($"file picker bounds: hook unavailable ({Marshal.GetLastWin32Error()})");
    }

    private void OnWindowShown(nint eventHook, uint eventType, nint window, int objectId,
        int childId, uint eventThread, uint eventTime)
    {
        try
        {
            if (handled || hook == 0 || eventType != ObjectShow || objectId != 0 || childId != 0
                || window == 0 || window == owner || GetWindow(window, 4) != owner) return; // GW_OWNER
            var className = new StringBuilder(64);
            if (GetClassName(window, className, className.Capacity) == 0 || className.ToString() != "#32770") return;
            // The common item dialog's shell view distinguishes it from an error
            // or confirmation dialog which can also use the #32770 window class.
            if (FindWindowEx(window, 0, "DUIViewWndClassName", null) == 0) return;
            if (!GetWindowRect(owner, out var ownerRect) || !GetWindowRect(window, out var currentRect)) return;
            var monitor = MonitorFromWindow(owner, 2); // MONITOR_DEFAULTTONEAREST
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;

            handled = true;
            // Stop listening before resizing: no reentrancy or later size enforcement.
            Dispose();
            var dpi = GetDpiForWindow(owner);
            var maximized = IsZoomed(window);
            var target = FilePickerPlacement.Fit(currentRect.Bounds, ownerRect.Bounds, info.Work.Bounds, dpi, maximized);
            if (target is not { } bounds) return;
            if (maximized) ShowWindowAsync(window, 9); // SW_RESTORE, including a broker thread
            var applied = SetWindowPos(window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                0x0004 | 0x0010 | 0x0200 | 0x4000); // NOZORDER | NOACTIVATE | NOOWNERZORDER | ASYNCWINDOWPOS
            System.Diagnostics.Trace.WriteLine($"file picker bounds: {currentRect.Bounds.Width}x{currentRect.Bounds.Height} -> "
                + $"{bounds.Width}x{bounds.Height}; dpi={dpi}; requested={applied}");
        }
        catch (Exception error)
        {
            // A failed placement must not break file selection or escape into User32.
            System.Diagnostics.Trace.WriteLine($"file picker bounds failed: {error.GetType().Name}");
        }
    }

    public void Dispose()
    {
        if (hook == 0) return;
        if (!handled) System.Diagnostics.Trace.WriteLine("file picker bounds: native picker window was not observed");
        UnhookWinEvent(hook);
        hook = 0;
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly PickerBounds Bounds => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId,
        int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
