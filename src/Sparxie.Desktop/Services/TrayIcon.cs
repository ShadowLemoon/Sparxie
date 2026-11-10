using System.Runtime.InteropServices;

namespace Sparxie.Desktop.Services;

/// <summary>
/// 系统托盘图标（Win32 Shell_NotifyIcon）。WinUI 3 没有内置托盘，这里用一个仅消息的隐藏窗口接收回调：
/// 左键单击/双击 → 显示主窗口；右键 → 菜单（显示 Sparxie / 退出）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_APP_TRAY = 0x8000 + 1; // WM_APP + 1
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_DESTROY = 0x0002;

    private const uint NIM_ADD = 0, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;

    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100;
    private const uint CmdShow = 1, CmdExit = 2;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly WndProc _wndProc; // 持有委托，避免被 GC
    private readonly string _tip;
    private readonly Action _onShow;
    private readonly Action _onExit;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _visible;
    private bool _disposed;

    public TrayIcon(string tip, Action onShow, Action onExit)
    {
        _tip = tip;
        _onShow = onShow;
        _onExit = onExit;
        _wndProc = WindowProc;
    }

    public bool IsVisible => _visible;

    public void Show()
    {
        if (_visible || _disposed)
        {
            return;
        }

        EnsureWindow();
        _icon = LoadAppIcon();
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = _icon;
        data.szTip = _tip;
        _visible = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        var data = CreateData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _visible = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        var className = "SparxieTrayWindow";
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClass(ref wc); // 重复注册会失败，忽略即可
        _hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private static IntPtr LoadAppIcon()
    {
        // 托盘图标只有 16–24px（随 DPI），直接从 ico 里按系统小图标尺寸取对应的那一帧，
        // 而不是取 32px 再缩小（缩小后细节全糊）；ico 缺失时依次回退到 exe 自带图标、系统默认图标。
        var ico = Path.Combine(AppContext.BaseDirectory, "sparxie.ico");
        if (File.Exists(ico))
        {
            var exact = LoadImage(IntPtr.Zero, ico, IMAGE_ICON, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_LOADFROMFILE);
            if (exact != IntPtr.Zero)
            {
                return exact;
            }
        }

        var path = Environment.ProcessPath;
        var icon = path is null ? IntPtr.Zero : ExtractIcon(IntPtr.Zero, path, 0);
        return icon.ToInt64() > 1 ? icon : LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_APP_TRAY)
        {
            switch ((uint)lParam.ToInt64())
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    _onShow();
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    ShowMenu();
                    return IntPtr.Zero;
            }
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, CmdShow, "显示 Sparxie");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CmdExit, "退出");
        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd); // 否则菜单点外面不会自动收起
        var command = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (command == CmdShow)
        {
            _onShow();
        }
        else if (command == CmdExit)
        {
            _onExit();
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
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
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
