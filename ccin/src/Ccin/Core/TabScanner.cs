using System.Diagnostics;
using System.Windows.Automation;

namespace Ccin.Core;

/// <summary>
/// 端末ウィンドウのタブを UI Automation で走査する。
///
/// Win32 のウィンドウ列挙ではタブが見えないため、ここだけ UIA を使う。
/// UIA は Win32 より重いので、呼ぶ回数は呼び出し側で絞ること。
/// </summary>
internal sealed class TabScanner
{
    /// <summary>
    /// 送信先にしてよいウィンドウを所有するプロセス。
    ///
    /// 線引きは「**端末アプリ自身が所有するウィンドウ**か」。他のアプリに埋め込まれた端末
    /// （VS Code / Cursor の内蔵ターミナル）は対象外とする。実測で、これらのウィンドウは
    /// UI Automation にタブ要素を 1 つも出さず、エディタとターミナルのどちらに
    /// フォーカスがあるかを外から判定できないことが判っている。前面化して貼り付けると
    /// ソースコードへ本文を書き込む事故になるため、最初から候補に入れない。
    ///
    /// `cmd` / `powershell` / `pwsh` は実測ではウィンドウを所有しない（所有者は
    /// `WindowsTerminal` か `conhost`）。照合が成立することはないが、既定のターミナル設定を
    /// 変えた環境で拾える可能性を残して置いてある。
    /// </summary>
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "wt",
        "cmd", "powershell", "pwsh", "conhost",
        "wezterm-gui", "alacritty", "mintty", "ConEmu", "ConEmu64", "Hyper", "Tabby", "warp",
    };

    private readonly VirtualDesktopService _desktops;

    public TabScanner(VirtualDesktopService desktops) => _desktops = desktops;

    /// <summary>
    /// 指定した仮想デスクトップ上の端末タブを、ウィンドウの Z オーダー順（手前から）に返す。
    /// </summary>
    /// <param name="desktopId">この GUID に属するウィンドウだけを対象にする。</param>
    /// <param name="excludeProcessId">自分自身のプロセス。送信先にしても意味がない。</param>
    public List<TerminalTab> Scan(Guid desktopId, uint excludeProcessId) =>
        ScanCore(desktopId, excludeProcessId);

    /// <summary>
    /// 所属デスクトップを問わず、全ての端末タブを返す。診断専用。
    /// 送信先の一覧には使わない（別デスクトップの端末は要求上の対象外）。
    /// </summary>
    public List<TerminalTab> ScanAll(uint excludeProcessId) =>
        ScanCore(null, excludeProcessId);

    private List<TerminalTab> ScanCore(Guid? desktopId, uint excludeProcessId)
    {
        var tabs = new List<TerminalTab>();

        foreach (IntPtr hWnd in NativeMethods.EnumerateVisibleWindows())
        {
            uint pid = NativeMethods.GetProcessId(hWnd);
            if (pid == excludeProcessId) continue;

            Guid? id = _desktops.GetDesktopId(hWnd);
            if (id is null) continue;
            if (desktopId is not null && id.Value != desktopId.Value) continue;

            string processName = TryGetProcessName(pid);
            if (processName.Length == 0) continue;
            if (!TerminalProcesses.Contains(processName)) continue;

            tabs.AddRange(ScanWindow(hWnd, pid, processName, id.Value));
        }

        return tabs;
    }

    /// <summary>1 ウィンドウ分のタブを取り出す。</summary>
    private static List<TerminalTab> ScanWindow(IntPtr hWnd, uint pid, string processName, Guid desktopId)
    {
        var result = new List<TerminalTab>();

        AutomationElement? root;
        try
        {
            root = AutomationElement.FromHandle(hWnd);
        }
        catch
        {
            return result;
        }
        if (root is null) return result;

        AutomationElementCollection items;
        try
        {
            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.TabItem);
            items = root.FindAll(TreeScope.Descendants, condition);
        }
        catch
        {
            return result;
        }

        // タブが取れない端末（タブの概念がない cmd 等）はウィンドウ自体を 1 タブとして扱う
        if (items.Count == 0)
        {
            string title = NativeMethods.GetWindowTitle(hWnd);
            if (title.Length == 0) return result;

            result.Add(new TerminalTab
            {
                WindowHandle = hWnd,
                ProcessId = pid,
                ProcessName = processName,
                Index = 0,
                Name = title,
                IsSelected = true,
                RuntimeId = string.Empty,
                DesktopId = desktopId,
                IsSingleTabWindow = true,
                HasTabElement = false,
            });
            return result;
        }

        for (int i = 0; i < items.Count; i++)
        {
            AutomationElement item = items[i];
            string name;
            bool selected = false;
            string runtimeId = string.Empty;

            try
            {
                name = item.Current.Name ?? string.Empty;
            }
            catch
            {
                continue;   // 走査中にタブが閉じられた
            }
            if (name.Length == 0) continue;

            try
            {
                if (item.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern pattern)
                {
                    selected = pattern.Current.IsSelected;
                }
            }
            catch
            {
                // パターン非対応。選択状態は不明のままにする
            }

            try
            {
                int[]? id = item.GetRuntimeId();
                if (id is not null) runtimeId = string.Join('.', id);
            }
            catch
            {
                // 取れなければ空のまま。同一性判定は別のキーで代替する
            }

            result.Add(new TerminalTab
            {
                WindowHandle = hWnd,
                ProcessId = pid,
                ProcessName = processName,
                Index = i,
                Name = name,
                IsSelected = selected,
                RuntimeId = runtimeId,
                DesktopId = desktopId,
                IsSingleTabWindow = items.Count == 1,
                HasTabElement = true,
            });
        }

        return result;
    }

    /// <summary>指定タブを前面に出す。ウィンドウの前面化も行う。</summary>
    public static bool SelectTab(TerminalTab tab) => SelectTab(tab, out _);

    /// <summary>
    /// 指定タブを前面に出す。<paramref name="detail"/> に経緯を残す。
    ///
    /// 経緯を返すのは、複数タブの窓でだけ通るタブ切り替えの経路が、
    /// 「貼り付いたのに Enter が効かない」事象と関係している疑いがあるため。
    /// </summary>
    public static bool SelectTab(TerminalTab tab, out string detail)
    {
        if (!tab.IsAlive)
        {
            detail = "target not alive";
            return false;
        }

        if (tab.IsSingleTabWindow)
        {
            detail = "single tab window; no tab switch";
        }
        else if (TrySelectTabItem(tab))
        {
            detail = "tab switched via UI Automation";
        }
        else
        {
            detail = "tab switch FAILED";
            return false;
        }

        bool activated = NativeMethods.Activate(tab.WindowHandle);
        detail += activated ? "; window activated" : "; window activation FAILED";
        if (!activated) return false;

        // タブを切り替えた直後は、フォーカスがタブ見出し側に残ることがある。
        // その状態だと貼り付け（ウィンドウ側で処理される）は効くのに、
        // Enter がタブ見出しへ行って端末に届かない。明示的に端末本体へ移す。
        detail += FocusTerminalContent(tab.WindowHandle)
            ? "; focus moved to terminal"
            : "; could not focus terminal";

        return true;
    }

    /// <summary>
    /// ウィンドウ内の端末本体にキーボードフォーカスを移す。
    ///
    /// 複数タブの窓では TermControl がタブの数だけ存在する。表示されていないものを
    /// 掴むと意味がないので、画面に出ているものを選ぶ。
    /// </summary>
    private static bool FocusTerminalContent(IntPtr hWnd)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(hWnd);
            if (root is null) return false;

            var condition = new PropertyCondition(AutomationElement.ClassNameProperty, "TermControl");
            AutomationElementCollection candidates = root.FindAll(TreeScope.Descendants, condition);

            foreach (AutomationElement candidate in candidates)
            {
                try
                {
                    if (candidate.Current.IsOffscreen) continue;
                    candidate.SetFocus();
                    return true;
                }
                catch
                {
                    // この要素には移せない。次を試す
                }
            }
        }
        catch
        {
            // UIA が応答しない等。前面化自体は済んでいるので送信は続ける
        }
        return false;
    }

    private static bool TrySelectTabItem(TerminalTab tab)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(tab.WindowHandle);
            if (root is null) return false;

            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.TabItem);
            AutomationElementCollection items = root.FindAll(TreeScope.Descendants, condition);

            AutomationElement? found = FindMatchingTab(items, tab);
            if (found is null) return false;

            if (found.GetCurrentPattern(SelectionItemPattern.Pattern) is SelectionItemPattern pattern)
            {
                pattern.Select();
                return true;
            }
        }
        catch
        {
            // 落ちた場合は呼び出し側が前面化の検証で気付く
        }
        return false;
    }

    /// <summary>
    /// 多段フォールバックでタブを同定する。
    /// RuntimeId が最も確実だが、Windows Terminal の実装やバージョンで
    /// 挙動が変わりうるため、名前・位置で順に代替する。
    /// </summary>
    private static AutomationElement? FindMatchingTab(AutomationElementCollection items, TerminalTab tab)
    {
        if (tab.RuntimeId.Length > 0)
        {
            foreach (AutomationElement item in items)
            {
                try
                {
                    int[]? id = item.GetRuntimeId();
                    if (id is not null && string.Join('.', id) == tab.RuntimeId) return item;
                }
                catch { }
            }
        }

        foreach (AutomationElement item in items)
        {
            try
            {
                if (item.Current.Name == tab.Name) return item;
            }
            catch { }
        }

        if (tab.Index >= 0 && tab.Index < items.Count) return items[tab.Index];

        return null;
    }

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
