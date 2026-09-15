using System.Runtime.InteropServices;
using System.Text;

namespace Ccin.Core;

/// <summary>
/// Win32 API の薄いラッパー。ここより上の層に P/Invoke を漏らさない。
/// </summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// 窓が乗っているモニタの実際の DPI。窓自身の DPI と食い違うことがあり、
    /// その場合 Windows は差分の倍率で窓全体を拡大縮小して描画する。
    /// </summary>
    public static uint GetMonitorDpi(IntPtr hWnd)
    {
        IntPtr monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return 0;
        if (GetDpiForMonitor(monitor, 0, out uint dpiX, out _) != 0) return 0;
        return dpiX;
    }

    // ---- 便利関数 -------------------------------------------------------

    public static string GetWindowTitle(IntPtr hWnd)
    {
        int length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 2);
        GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public static uint GetProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid;
    }

    /// <summary>
    /// 他プロセスのウィンドウを前面に出す。SetForegroundWindow だけでは
    /// フォアグラウンドロックで無視されるため、入力スレッドを一時的に繋ぐ。
    /// </summary>
    public static bool Activate(IntPtr hWnd)
    {
        if (!IsWindow(hWnd)) return false;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();

        bool attached = false;
        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, foregroundThread, true);
        }

        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);

        if (attached)
        {
            AttachThreadInput(currentThread, foregroundThread, false);
        }

        return GetForegroundWindow() == hWnd;
    }

    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    /// <summary>
    /// 今出ているポップアップ類（IME の変換候補・メニュー・ツールチップ）を列挙する。
    ///
    /// Enter が端末に届かない事象で、キーを横取りしている窓が無いかを見るための診断。
    /// 常時出ている全画面の入力ホストは除き、実際に何かを表示している小さな窓だけを拾う。
    /// </summary>
    public static string DescribePopups()
    {
        var found = new List<string>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            string className = GetClassNameOf(hWnd);
            bool popupish =
                className.Contains("tooltip", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("Popup", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("Xaml", StringComparison.OrdinalIgnoreCase) ||
                className == "#32768";                    // 標準メニュー
            if (!popupish) return true;

            if (!GetWindowRect(hWnd, out RECT rect)) return true;
            if (rect.Width <= 0 || rect.Height <= 0) return true;

            // 全画面サイズの入力ホストは常時出ているので除く
            if (rect.Width >= 1900 && rect.Height >= 1000) return true;

            string process = SafeProcessName(GetProcessId(hWnd));
            found.Add($"{className}@{process} ({rect.Left},{rect.Top}) {rect.Width}x{rect.Height}");
            return true;
        }, IntPtr.Zero);

        return found.Count == 0 ? "(none)" : string.Join(" | ", found);
    }

    /// <summary>
    /// 対象ウィンドウのスレッドでフォーカス・キャレットがどこにあるか。
    /// IME が変換中だとキャレット位置が動くため、あわせて見ると状況が判る。
    /// </summary>
    public static string DescribeGuiThread(IntPtr hWnd)
    {
        try
        {
            uint threadId = GetWindowThreadProcessId(hWnd, out _);
            var info = new GUITHREADINFO();
            info.cbSize = Marshal.SizeOf(info);
            if (!GetGUIThreadInfo(threadId, ref info)) return "(unavailable)";

            IntPtr ime = ImmGetDefaultIMEWnd(hWnd);
            return $"focus=0x{info.hwndFocus.ToInt64():X} caret=0x{info.hwndCaret.ToInt64():X} " +
                   $"flags=0x{info.flags:X} imeWnd=0x{ime.ToInt64():X}";
        }
        catch
        {
            return "(failed)";
        }
    }

    private static string GetClassNameOf(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string SafeProcessName(uint processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>可視でタイトルを持つトップレベルウィンドウを Z オーダー順（手前から）に返す。</summary>
    public static List<IntPtr> EnumerateVisibleWindows()
    {
        var result = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0)
            {
                result.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
