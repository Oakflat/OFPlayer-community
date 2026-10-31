using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.Views;
/// <summary>Raw HWND + windowed WebView2 controller. No XAML or visual bridge.</summary>
internal sealed class NativeCapsuleHost
{
    private static readonly Dictionary<nint, NativeCapsuleHost> windows = new();
    private static readonly WindowProc procedure = WndProc;
    private static ushort windowClass;
    private CoreWebView2Controller? controller;
    private CoreWebView2? core;
    private bool closed;
    private bool backgroundMode;
    private CoreWebView2MemoryUsageTargetLevel? appliedMemoryTarget;
    internal void SetBackgroundMode(bool background)
    {
        backgroundMode = background;
        ApplyMemoryTarget();
    }

    private void ApplyMemoryTarget()
    {
        if (closed || !lifetime.Ready || core is null)
            return;
        try
        {
            var target = lifetime.Requested ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
            core.MemoryUsageTargetLevel = target;
            if (appliedMemoryTarget != target)
                PreviewLog.Write($"capsule memory target: {target}; windowBackground={backgroundMode}");
            appliedMemoryTarget = target;
        }
        catch (Exception error)
        {
            PreviewLog.Write($"capsule memory hint unavailable: {error.Message}");
        }
    }

    private readonly OFPlayer.WinUI.Core.Playback.CapsuleLifetime lifetime = new();
    private bool ignoresMouse;
    private double hitWidth = 540;
    private double cssViewportWidth;
    private double cssViewportHeight;
    private double collapsedHitHeight;
    private double expandedHitHeight;
    private bool expanded;
    private TaskCompletionSource? dragCompletion;
    private long dragStartedAt;
    private Windows.Graphics.PointInt32 dragStartPosition;
    private Point dragOrigin;
    private int dragMoveCount;
    private int dragAppliedCount;
    private const nuint DragTimer = 0xCA51;
    private readonly OFPlayer.WinUI.Core.Playback.CapsulePointerRegion pointerRegion = new();
    private readonly DispatcherQueueTimer mouseTimer;
    public nint Handle { get; }
    public AppWindow AppWindow { get; }
    public DispatcherQueue DispatcherQueue { get; } = DispatcherQueue.GetForCurrentThread();
    public CoreWebView2? CoreWebView2 => core;
    public bool IsRequested => lifetime.Requested;
    public bool IsShown => lifetime.Visible;
    /// <summary>C0 census: how many native capsule HWNDs are alive right now.</summary>
    public static int LiveWindowCount => windows.Count;

    public event EventHandler? Closed;
    /// <summary>The page's CSS px to physical px: monitor scale times Windows text size.</summary>
    public event EventHandler? ContentScaleChanged;
    public double ContentScale
    {
        get
        {
            var scale = controller?.RasterizationScale ?? 0;
            if (double.IsFinite(scale) && scale > 0)
                return scale;
            var dpi = GetDpiForWindow(Handle);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
    }

    public NativeCapsuleHost()
    {
        if (windowClass == 0)
        {
            var wc = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = GetModuleHandle(null),
                Name = "OFPlayer.NativeLyricCapsule",
                Cursor = LoadCursor(0, (nint)32512),
                Background = GetStockObject(4),
            };
            windowClass = RegisterClassEx(ref wc);
            if (windowClass == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        // TOOLWINDOW / TOPMOST / NOACTIVATE. The web canvas is never region-clipped.
        Handle = CreateWindowEx(0x08000088, "OFPlayer.NativeLyricCapsule", "OFPlayer Lyric Capsule", 0x80000000, 0, 0, 560, 112, 0, 0, GetModuleHandle(null), 0);
        if (Handle == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        windows.Add(Handle, this);
        AppWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(Handle));
        ConfigureTransparency();
        mouseTimer = DispatcherQueue.CreateTimer();
        mouseTimer.Interval = TimeSpan.FromMilliseconds(32);
        mouseTimer.Tick += (_, _) =>
        {
            if (dragCompletion is null)
                UpdateMousePassthrough();
        };
        PreviewLog.Write($"capsule native HWND created: {Handle}; no XAML host");
    }

    public async Task EnsureCoreWebView2Async()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.EnsureCoreWebView2Async");
        var environment = await WebViewEnvironmentProvider.GetAsync();
        PreviewLog.Write("capsule native environment ready");
        if (closed)
            return;
        CoreWebView2Controller next;
        using (OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("capsule.controller.create"))
            next = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)Handle));
        PreviewLog.Write("capsule native controller ready");
        if (closed)
        {
            next.Close();
            return;
        }

        controller = next;
        core = controller.CoreWebView2;
        controller.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        controller.IsVisible = true;
        controller.RasterizationScaleChanged += (_, _) => ContentScaleChanged?.Invoke(this, EventArgs.Empty);
        ResizeWeb();
    }

    public void ShowReady()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.ShowReady");
        if (closed)
            return;
        lifetime.Loaded();
        ApplyVisibility();
        PreviewLog.Write($"capsule native warm ready; visible={lifetime.Visible}");
    }

    public void Activate()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.Activate");
        if (closed)
            return;
        lifetime.Show();
        ApplyVisibility();
        PreviewLog.Write($"capsule native show requested; warm={lifetime.Ready}");
    }

    public void Hide()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.Hide");
        EndDrag();
        lifetime.Hide();
        ApplyVisibility();
        PreviewLog.Write("capsule native hidden; controller retained");
    }

    private void ApplyVisibility()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.ApplyVisibility");
        if (closed)
            return;
        ApplyMemoryTarget();
        if (controller is not null)
            controller.IsVisible = lifetime.Visible || !lifetime.Ready;
        ShowWindow(Handle, lifetime.Visible ? 4 : 0);
        if (lifetime.Visible)
            mouseTimer.Start();
        else
            mouseTimer.Stop();
    }

    public void Close()
    {
        if (!closed)
            DestroyWindow(Handle);
    }

    public Task StartDraggingAsync(double? clientX = null, double? clientY = null)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.StartDraggingAsync");
        // A NOACTIVATE WebView popup cannot rely on foreground mouse capture.
        // Track the physical pointer without entering DefWindowProc's modal move
        // loop; keep the UI/WebView message pump running throughout the gesture.
        if (closed || !lifetime.Visible || (GetAsyncKeyState(1) & 0x8000) == 0 || !GetCursorPos(out var pointer))
            return Task.CompletedTask;
        if (dragCompletion is not null)
            return dragCompletion.Task;
        if (!GetWindowRect(Handle, out var startBounds))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        dragCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = dragCompletion.Task;
        dragStartedAt = Environment.TickCount64;
        dragPerfStarted = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.NowMs;
        previousDragPerfTick = 0;
        dragStartPosition = new(startBounds.Left, startBounds.Top);
        var scale = ContentScale;
        // The browser-to-host bridge is asynchronous. The pointer may already
        // have moved by now, so preserve the original DOM pointerdown offset.
        dragOrigin = clientX is double x && clientY is double y && double.IsFinite(x) && double.IsFinite(y) ? new Point
        {
            X = startBounds.Left + (int)Math.Round(x * scale),
            Y = startBounds.Top + (int)Math.Round(y * scale)
        }

        : pointer;
        dragMoveCount = 0;
        dragAppliedCount = 0;
        UpdateMousePassthrough();
        // Use HWND messages so tracking does not depend on DispatcherQueueTimer
        // scheduling. Do not steal the child's capture: it must receive
        // pointerup for its own pressed state.
        if (SetTimer(Handle, DragTimer, 10, 0) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            EndDrag();
            throw new System.ComponentModel.Win32Exception(error);
        }

        ApplyDragPosition(pointer);
        PreviewLog.Write("capsule drag started: physical pointer tracking");
        return completion;
    }

    private void EndDrag()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.EndDrag");
        var completion = dragCompletion;
        KillTimer(Handle, DragTimer);
        dragCompletion = null;
        if (completion is not null)
        {
            var gesture = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Activity("gesture.capsule.move", dragPerfStarted, "gesture");
            OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Sample("capsule.drag.appliedMoves", dragAppliedCount, gesture);
            completion.TrySetResult();
            controller?.NotifyParentWindowPositionChanged();
            GetWindowRect(Handle, out var position);
            PreviewLog.Write($"capsule native drag end: elapsedMs={Environment.TickCount64 - dragStartedAt}; moves={dragMoveCount}; applied={dragAppliedCount}; delta={position.Left - dragStartPosition.X},{position.Top - dragStartPosition.Y}");
        }
    }

    private void UpdateDrag()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.UpdateDrag");
        if (dragCompletion is null)
            return;
        var now = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.NowMs;
        if (previousDragPerfTick > 0)
            OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Sample("capsule.drag.timerGapMs", now - previousDragPerfTick);
        previousDragPerfTick = now;
        if (closed || !lifetime.Visible || (GetAsyncKeyState(27) & 0x8000) != 0 || !GetCursorPos(out var pointer))
        {
            EndDrag();
            return;
        }

        // Sample once on release too, so a quick gesture commits its final
        // position. EndDrag removes the timer before subsequent cursor motion.
        ApplyDragPosition(pointer);
        if ((GetAsyncKeyState(1) & 0x8000) == 0)
            EndDrag();
    }

    private void ApplyDragPosition(Point pointer)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.ApplyDragPosition");
        if (dragCompletion is null || closed || !lifetime.Visible)
            return;
        dragMoveCount++;
        var x = dragStartPosition.X + pointer.X - dragOrigin.X;
        var y = dragStartPosition.Y + pointer.Y - dragOrigin.Y;
        if (!GetWindowRect(Handle, out var before) || (before.Left == x && before.Top == y))
            return;
        var moveStarted = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.NowMs;
        var moved = SetWindowPos(Handle, 0, x, y, 0, 0, 0x15);
        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Activity("capsule.drag.setWindowPos", moveStarted);
        if (!moved)
        {
            PreviewLog.Write($"capsule move failed: win32={Marshal.GetLastWin32Error()}");
            return;
        }

        if (GetWindowRect(Handle, out var after) && (after.Left != before.Left || after.Top != before.Top))
            dragAppliedCount++;
    }

    private double dragPerfStarted, previousDragPerfTick;
    public void ShowFailure(string detail)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.ShowFailure");
        if (closed)
            return;
        if (lifetime.Requested)
            MessageBox(0, Localization.Get("capsule.failure.message") + "\n\n" + detail, Localization.Get("capsule.failure.title"), 0x10);
        else
            PreviewLog.Write($"capsule prewarm failed: {detail}");
        Close();
    }

    public void SetHitRegion(double width, bool isExpanded, double viewportWidth = 0, double viewportHeight = 0, double collapsedHeight = 0, double expandedHeight = 0)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.SetHitRegion");
        hitWidth = double.IsFinite(width) ? Math.Clamp(width, 1, 540) : 540;
        cssViewportWidth = double.IsFinite(viewportWidth) && viewportWidth is> 0 and <= 16384 ? viewportWidth : 0;
        cssViewportHeight = double.IsFinite(viewportHeight) && viewportHeight is> 0 and <= 16384 ? viewportHeight : 0;
        // Out-of-range heights fall back to the region's defaults there.
        collapsedHitHeight = collapsedHeight;
        expandedHitHeight = expandedHeight;
        expanded = isExpanded;
        UpdateMousePassthrough();
    }

    private void UpdateMousePassthrough()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.UpdateMousePassthrough");
        if (closed || !GetCursorPos(out var point) || !ScreenToClient(Handle, ref point) || !GetClientRect(Handle, out var bounds))
            return;
        var scale = ContentScale;
        var ignore = dragCompletion is null && !pointerRegion.AcceptsPhysical(point.X, point.Y, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, scale, hitWidth, expanded, cssViewportWidth, cssViewportHeight, collapsedHitHeight, expandedHitHeight);
        if (ignore == ignoresMouse)
            return;
        var style = GetWindowLongPtr(Handle, -20).ToInt64();
        const long passthrough = 0x00080020; // LAYERED + TRANSPARENT, as Tao's cursor hit-test path.
        SetWindowLongPtr(Handle, -20, (nint)(ignore ? style | passthrough : style & ~passthrough));
        if (ignore)
            SetLayeredWindowAttributes(Handle, 0, 255, 2);
        ignoresMouse = ignore;
    }

    private void ResizeWeb()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.ResizeWeb");
        if (controller is not null && GetClientRect(Handle, out var rect))
            controller.Bounds = new Windows.Foundation.Rect(0, 0, rect.Right, rect.Bottom);
    }

    private void ConfigureTransparency()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("NativeCapsuleHost.ConfigureTransparency");
        // Same DWM transparent-client mechanism used by Tao. Not acrylic.
        var region = CreateRectRgn(0, 0, -1, -1);
        if (region == 0)
            throw new InvalidOperationException("Cannot create transparent client region.");
        try
        {
            var blur = new BlurBehind
            {
                Flags = 3,
                Enable = 1,
                Region = region
            };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(Handle, ref blur));
        }
        finally
        {
            DeleteObject(region);
        }

        var noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(Handle, 34, ref noBorder, 4);
        var noRounding = 1;
        DwmSetWindowAttribute(Handle, 33, ref noRounding, 4);
    }

    private static nint WndProc(nint hwnd, uint msg, nuint wp, nint lp)
    {
        try
        {
            if (windows.TryGetValue(hwnd, out var host))
            {
                switch (msg)
                {
                    case 0x0010:
                        host.Hide();
                        return 0;
                    case 0x0005:
                        host.ResizeWeb();
                        break;
                    case 0x0003:
                        if (host.dragCompletion is null)
                            host.controller?.NotifyParentWindowPositionChanged();
                        break;
                    case 0x0113: // WM_TIMER
                        if (wp == DragTimer)
                        {
                            host.UpdateDrag();
                            return 0;
                        }

                        break;
                    case 0x001F:
                        host.EndDrag();
                        break; // WM_CANCELMODE
                    case 0x031E:
                        host.ConfigureTransparency();
                        break;
                    case 0x02E0:
                        var rect = Marshal.PtrToStructure<Rect>(lp);
                        SetWindowPos(hwnd, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);
                        host.ResizeWeb();
                        break;
                    case 0x0014:
                        if (GetClientRect(hwnd, out var area))
                            FillRect((nint)wp, ref area, GetStockObject(4));
                        return 1;
                    case 0x0021:
                        return 3; // MA_NOACTIVATE
                    case 0x0082:
                        host.closed = true;
                        host.EndDrag();
                        host.lifetime.Close();
                        host.mouseTimer.Stop();
                        try
                        {
                            host.controller?.Close();
                        }
                        finally
                        {
                            host.controller = null;
                            host.core = null;
                            windows.Remove(hwnd);
                            host.Closed?.Invoke(host, EventArgs.Empty);
                        }

                        break;
                }
            }
        }
        catch (Exception error)
        {
            PreviewLog.Write($"capsule native message {msg:X}: {error}");
        }

        return DefWindowProc(hwnd, msg, wp, lp);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Menu;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Name;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        public uint Flags;
        public int Enable;
        public nint Region;
        public int Transition;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int mode);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint hwnd, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")]
    private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")]
    private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hwnd, string text, string caption, uint flags);
    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);
    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(nint hwnd, ref BlurBehind blur);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
