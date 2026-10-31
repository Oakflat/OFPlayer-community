using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OFPlayer_WinUI.Services;
internal enum TrayCommand
{
    Open = 1,
    PlayPause,
    Previous,
    Next,
    Capsule,
    Exit
}

/// <summary>A hidden native message window owns the notification icon, independently of XAML visibility.
/// The app draws the icon's menu itself, at the screen point this host reports.</summary>
internal sealed class TrayIconHost : IDisposable
{
    internal static readonly string WindowClassName = "OFPlayer.Community.Tray." + WindowsIdentity.GetCurrent().User?.Value;
    private const uint CallbackMessage = 0x8001;
    private static readonly uint ActivationMessage = RegisterWindowMessage("OFPlayer.Community.Activate.v1");
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly WindowProc procedure;
    private readonly Action<TrayCommand> dispatch;
    private readonly Action<int, int> openMenu;
    private readonly Action<Exception> recovery;
    private readonly string className;
    private nint window, icon;
    private bool registered, disposed;
    public TrayIconHost(Action<TrayCommand> dispatch, Action<int, int> openMenu, Action<Exception> recovery, string? className = null)
    {
        this.dispatch = dispatch;
        this.openMenu = openMenu;
        this.recovery = recovery;
        this.className = className ?? WindowClassName;
        procedure = WindowProcedure;
        var instance = GetModuleHandle(null);
        var type = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
            Instance = instance,
            Name = this.className,
        };
        if (RegisterClassEx(ref type) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        window = CreateWindowEx(0x80, this.className, "OFPlayer", 0x80000000, 0, 0, 0, 0, 0, 0, instance, 0);
        if (window == 0)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            UnregisterClass(this.className, instance);
            throw error;
        }
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (registered)
            return;
        if (icon == 0)
        {
            icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), 1, 0, 0, 0x10 | 0x40);
            if (icon == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the tray icon.");
        }

        var data = IconData();
        if (!ShellNotifyIcon(0, ref data))
            throw new InvalidOperationException("Windows could not add the OFPlayer tray icon.");
        registered = true;
        data.Version = 4;
        if (!ShellNotifyIcon(4, ref data))
        {
            Hide();
            throw new InvalidOperationException("Windows could not configure the OFPlayer tray icon.");
        }
    }

    public void Hide()
    {
        if (!registered)
            return;
        var data = IconData();
        ShellNotifyIcon(2, ref data);
        registered = false;
    }

    public static bool ActivateExisting(string? className = null)
    {
        var target = FindWindow(className ?? WindowClassName, null);
        if (target == 0 || ActivationMessage == 0)
            return false;
        GetWindowThreadProcessId(target, out var pid);
        AllowSetForegroundWindow(pid);
        return PostMessage(target, ActivationMessage, 0, 0);
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = window,
        Id = 1,
        Flags = 1 | 2 | 4 | 0x80,
        Callback = CallbackMessage,
        Icon = icon,
        Tip = "OFPlayer Community",
        Info = string.Empty,
        InfoTitle = string.Empty,
    };
    private nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        // A managed exception must never escape a native window procedure.
        try
        {
            if (disposed)
                return DefWindowProc(hwnd, message, wParam, lParam);
            if (message == ActivationMessage)
            {
                dispatch(TrayCommand.Open);
                return 0;
            }

            if (message == TaskbarCreated && registered)
            {
                registered = false;
                try
                {
                    Show();
                }
                catch (Exception error)
                {
                    recovery(error);
                }

                return 0;
            }

            if (message == CallbackMessage)
            {
                var notification = (uint)(lParam.ToInt64() & 0xffff);
                if (notification is 0x400 or 0x401)
                    dispatch(TrayCommand.Open); // mouse or keyboard selection
                else if (notification == 0x7B)
                    OpenMenu(wParam);
                return 0;
            }

            if (message == 0x11)
                return 1; // WM_QUERYENDSESSION: allow Windows shutdown.
            if (message == 0x16 && wParam != 0)
                dispatch(TrayCommand.Exit);
        }
        catch (Exception error)
        {
            recovery(error);
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    // Version 4 puts the icon's anchor point in wParam; the keyboard may leave it unset.
    private void OpenMenu(nuint location)
    {
        var x = (int)(short)(location & 0xffff);
        var y = (int)(short)((location >> 16) & 0xffff);
        if (x == -1 && y == -1 && GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
        }

        openMenu(x, y);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        Hide();
        disposed = true;
        if (window != 0)
            DestroyWindow(window);
        window = 0;
        if (icon != 0)
            DestroyIcon(icon);
        icon = 0;
        UnregisterClass(className, GetModuleHandle(null));
        GC.KeepAlive(procedure);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? Menu, Name;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass type);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint ex, string type, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string type, string? name);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string path, uint type, int w, int h, uint flags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
}
