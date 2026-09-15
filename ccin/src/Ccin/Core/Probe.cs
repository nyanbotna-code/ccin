using System.Diagnostics;
using System.IO;
using System.Text;

namespace Ccin.Core;

/// <summary>
/// 開発時の診断。`ccin.exe --probe &lt;出力先&gt;` で走らせ、
/// 仮想デスクトップ判定とタブ走査が実機で動くかを実測値として残す。
/// </summary>
internal static class Probe
{
    public static void Run(string outputPath)
    {
        var log = new StringBuilder();
        void Line(string text) => log.AppendLine(text);

        Line("=== CCIN probe ===");
        Line($"ccin version : {AppInfo.Version}");
        Line($"runtime      : {Environment.Version}");
        Line($"os           : {Environment.OSVersion.Version}");
        Line($"process id   : {Environment.ProcessId}");
        Line("");

        var desktops = new VirtualDesktopService();
        Line($"virtual desktop COM : {(desktops.IsAvailable ? "OK" : "UNAVAILABLE")}");

        // 自分の所属デスクトップを知るには、実際に表示されたウィンドウが要る
        Guid myDesktop;
        using (var probeForm = new Form
        {
            Text = "ccin-probe",
            Width = 200,
            Height = 100,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false,
        })
        {
            probeForm.Show();
            Application.DoEvents();
            Thread.Sleep(400);

            Guid? id = desktops.GetDesktopId(probeForm.Handle);
            myDesktop = id ?? Guid.Empty;
            Line($"my desktop id       : {(id is null ? "(none)" : id.Value.ToString())}");
            Line($"on current desktop  : {desktops.IsOnCurrentDesktop(probeForm.Handle)}");

            probeForm.Close();
        }
        Line("");

        if (myDesktop == Guid.Empty)
        {
            Line("!! could not determine my own desktop id; aborting scan");
            File.WriteAllText(outputPath, log.ToString(), new UTF8Encoding(false));
            return;
        }

        // --- 全可視ウィンドウの所属デスクトップ ---
        Line("--- visible windows (front to back) ---");
        int windowCount = 0;
        var desktopCounts = new Dictionary<string, int>();
        foreach (IntPtr hWnd in NativeMethods.EnumerateVisibleWindows())
        {
            windowCount++;
            uint pid = NativeMethods.GetProcessId(hWnd);
            string processName = SafeProcessName(pid);
            Guid? id = desktops.GetDesktopId(hWnd);
            string key = id?.ToString()[..8] ?? "(none)";
            desktopCounts[key] = desktopCounts.GetValueOrDefault(key) + 1;

            if (IsInteresting(processName))
            {
                bool mine = id is not null && id.Value == myDesktop;
                Line($"  [{key}] sameGuid={mine,-5} onCurrent={desktops.IsOnCurrentDesktop(hWnd),-5} " +
                     $"{processName,-16} : {NativeMethods.GetWindowTitle(hWnd)}");
            }
        }
        Line($"  total visible windows: {windowCount}");
        foreach (var pair in desktopCounts.OrderByDescending(p => p.Value))
        {
            Line($"  desktop {pair.Key} -> {pair.Value} window(s)");
        }
        Line("");

        // --- タブ走査 ---
        var scanner = new TabScanner(desktops);
        var sw = Stopwatch.StartNew();
        List<TerminalTab> tabs = scanner.Scan(myDesktop, (uint)Environment.ProcessId);
        sw.Stop();

        Line($"--- terminal tabs on my desktop (scan took {sw.ElapsedMilliseconds} ms) ---");
        Line($"  {tabs.Count} tab(s)");
        foreach (TerminalTab tab in tabs)
        {
            string flags = tab.IsSelected ? "active" : "      ";
            string claude = tab.LooksLikeClaudeCode ? " <== Claude Code" : "";
            Line($"  {flags} hwnd={tab.WindowHandle.ToInt64(),-10} idx={tab.Index} " +
                 $"rid={(tab.RuntimeId.Length > 0 ? tab.RuntimeId : "-"),-20} " +
                 $"{tab.ProcessName} : {tab.Name}{claude}");
        }
        Line("");

        // 送信先の一覧は絞り込まない。ここは「どれが Claude Code に見えるか」の内訳
        var claudeTabs = tabs.Where(t => t.LooksLikeClaudeCode).ToList();
        Line($"--- of which look like Claude Code: {claudeTabs.Count} ---");
        for (int i = 0; i < claudeTabs.Count; i++)
        {
            Line($"  [{i + 1}] {claudeTabs[i].Name}");
        }
        Line("");

        // --- 2 回走査して RuntimeId が安定するか ---
        Thread.Sleep(500);
        List<TerminalTab> again = scanner.Scan(myDesktop, (uint)Environment.ProcessId);
        int stable = 0, moved = 0;
        foreach (TerminalTab before in tabs)
        {
            TerminalTab? after = again.FirstOrDefault(t =>
                t.RuntimeId.Length > 0 && t.RuntimeId == before.RuntimeId);
            if (after is not null) stable++; else moved++;
        }
        Line($"--- rescan after 500ms: {again.Count} tab(s), runtimeId matched {stable}, lost {moved} ---");

        File.WriteAllText(outputPath, log.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// タブ名を一定時間サンプリングする。`ccin.exe --titles &lt;出力先&gt; &lt;秒&gt;`。
    ///
    /// Claude Code はタイトル先頭の記号を回している。定義書は U+23FA 固定を前提にしていたが、
    /// 実測でそれ以外の記号が出たため、実際に使われる記号の集合を実測で確定する。
    /// ウィンドウを作らないので画面を邪魔しない。
    /// </summary>
    public static void SampleTitles(string outputPath, int seconds)
    {
        var seen = new Dictionary<string, int>();          // タブ名 -> 観測回数
        var leadingChars = new Dictionary<string, int>();  // 先頭文字 -> 観測回数
        var desktops = new VirtualDesktopService();
        var scanner = new TabScanner(desktops);

        DateTime end = DateTime.Now.AddSeconds(seconds);
        int rounds = 0;
        while (DateTime.Now < end)
        {
            rounds++;
            foreach (TerminalTab tab in scanner.Scan(AnyDesktop(desktops), 0))
            {
                seen[tab.Name] = seen.GetValueOrDefault(tab.Name) + 1;

                string head = tab.Name.Length > 0
                    ? char.ConvertFromUtf32(char.ConvertToUtf32(tab.Name, 0))
                    : string.Empty;
                if (head.Length > 0)
                {
                    leadingChars[head] = leadingChars.GetValueOrDefault(head) + 1;
                }
            }
            Thread.Sleep(150);
        }

        var log = new StringBuilder();
        log.AppendLine($"=== title sampling ({rounds} rounds over {seconds}s) ===");
        log.AppendLine("--- distinct tab names ---");
        foreach (var pair in seen.OrderByDescending(p => p.Value))
        {
            log.AppendLine($"  {pair.Value,4}x  {pair.Key}");
        }
        log.AppendLine("--- distinct leading characters ---");
        foreach (var pair in leadingChars.OrderByDescending(p => p.Value))
        {
            int codepoint = char.ConvertToUtf32(pair.Key, 0);
            log.AppendLine($"  {pair.Value,4}x  '{pair.Key}'  U+{codepoint:X4}");
        }

        File.WriteAllText(outputPath, log.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// 全デスクトップの端末タブを、送信先一覧と同じ並び順で出す。`ccin.exe --tabsall &lt;出力先&gt;`。
    ///
    /// send.log に残る hwnd と RuntimeId が、実際にどのウィンドウのどのタブなのかを
    /// 突き合わせるための診断。並び順が Z オーダー依存であることの確認にも使う。
    /// </summary>
    public static void ListAllTabs(string outputPath)
    {
        var log = new StringBuilder();
        var desktops = new VirtualDesktopService();
        var scanner = new TabScanner(desktops);

        List<TerminalTab> tabs = scanner.ScanAll((uint)Environment.ProcessId);

        log.AppendLine($"=== all terminal tabs ({DateTime.Now:HH:mm:ss}) ===");
        log.AppendLine("並びは EnumWindows の Z オーダー（手前から）。送信先一覧と同じ順序。");
        log.AppendLine("");

        int order = 0;
        foreach (TerminalTab tab in tabs)
        {
            order++;
            log.AppendLine(
                $"  #{order}  hwnd=0x{tab.WindowHandle.ToInt64():X}  idx={tab.Index}  " +
                $"rid={(tab.RuntimeId.Length > 0 ? tab.RuntimeId : "-")}");
            log.AppendLine(
                $"       claude={tab.LooksLikeClaudeCode}  single={tab.IsSingleTabWindow}  " +
                $"active={tab.IsSelected}  onCurrent={desktops.IsOnCurrentDesktop(tab.WindowHandle)}");
            log.AppendLine(
                $"       desktop={tab.DesktopId}");
            log.AppendLine(
                $"       name={tab.Name}");
        }

        // 送信先一覧には端末タブが全て載る。ここはそのうち Claude Code に見えるものの内訳
        var claudeTabs = tabs.Where(t => t.LooksLikeClaudeCode).ToList();
        log.AppendLine("");
        log.AppendLine($"--- 送信先一覧に載る候補: {tabs.Count} 件（デスクトップ横断。実際は自分の机の分だけ） ---");
        log.AppendLine($"--- うち Claude Code に見えるもの: {claudeTabs.Count} 件 ---");
        for (int i = 0; i < claudeTabs.Count; i++)
        {
            TerminalTab tab = claudeTabs[i];
            log.AppendLine($"  {i + 1}: {tab.Name}   [hwnd=0x{tab.WindowHandle.ToInt64():X} idx={tab.Index} rid={tab.RuntimeId}]");
        }

        File.WriteAllText(outputPath, log.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// DPI まわりの実測。`ccin.exe --dpi &lt;出力先&gt;`。
    ///
    /// 窓の DPI がモニタの DPI と食い違うと、Windows が窓全体をその比率で拡大縮小して
    /// 描画する。部品がはみ出す・保存したサイズどおりに開かない、の原因になる。
    /// アプリ自身が何を見ているかを外から測れないので、ここで出す。
    /// </summary>
    public static void DpiReport(string outputPath)
    {
        var log = new StringBuilder();

        log.AppendLine($"HighDpiMode          : {Application.HighDpiMode}");
        log.AppendLine($"GetDpiForSystem      : {NativeMethods.GetDpiForSystem()}");
        log.AppendLine("");

        log.AppendLine("--- screens (as the app sees them) ---");
        foreach (Screen screen in Screen.AllScreens)
        {
            log.AppendLine($"  {screen.DeviceName} primary={screen.Primary} " +
                           $"bounds={screen.Bounds} work={screen.WorkingArea}");
        }
        log.AppendLine("");

        using var form = new Form
        {
            Text = "ccin-dpi",
            Width = 300,
            Height = 200,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = false,
        };
        form.Show();
        Application.DoEvents();
        Thread.Sleep(400);

        log.AppendLine("--- a freshly created window ---");
        log.AppendLine($"  form.DeviceDpi     : {form.DeviceDpi}");
        log.AppendLine($"  GetDpiForWindow    : {NativeMethods.GetDpiForWindow(form.Handle)}");
        log.AppendLine($"  monitor under it   : {NativeMethods.GetMonitorDpi(form.Handle)}");
        log.AppendLine($"  bounds             : {form.Bounds}");
        log.AppendLine($"  LogicalToDevice 100: {form.LogicalToDeviceUnits(100)}");
        form.Close();

        File.WriteAllText(outputPath, log.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// CCIN 自身の窓の所属デスクトップを一覧する。`ccin.exe --windows &lt;出力先&gt;`。
    ///
    /// 「仮想デスクトップごとに窓 1 枚」を実測するための診断。
    /// PowerShell 5.1 は COM インターフェースへキャストできないので、判定はこちらに置く。
    /// </summary>
    public static void ListOwnWindows(string outputPath)
    {
        var log = new StringBuilder();
        var desktops = new VirtualDesktopService();

        log.AppendLine($"virtual desktop COM : {(desktops.IsAvailable ? "OK" : "UNAVAILABLE")}");
        log.AppendLine("--- CCIN windows ---");

        int count = 0;
        foreach (IntPtr hWnd in NativeMethods.EnumerateVisibleWindows())
        {
            string title = NativeMethods.GetWindowTitle(hWnd);
            if (!title.StartsWith(AppInfo.TitlePrefix, StringComparison.Ordinal)) continue;

            count++;
            Guid? id = desktops.GetDesktopId(hWnd);
            log.AppendLine($"  hwnd={hWnd.ToInt64(),-10} pid={NativeMethods.GetProcessId(hWnd),-6} " +
                           $"desktop={id?.ToString() ?? "(none)"} onCurrent={desktops.IsOnCurrentDesktop(hWnd)}");
        }

        log.AppendLine($"  total: {count}");
        File.WriteAllText(outputPath, log.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// 送信経路の実測。`ccin.exe --sendtest &lt;出力先&gt; &lt;タイトル部分一致&gt; &lt;本文&gt;`。
    ///
    /// 画面を操作せずに Core.Sender をそのまま通す。実セッションを汚さないよう、
    /// 使い捨ての端末タブを立ててからこれを当てること。
    ///
    /// 本文を <c>@&lt;パス&gt;</c> にするとファイルから読む。**複数行の本文はこちらを使う。**
    /// 改行を含む引数はコマンドライン経由で途中まで切り落とされ、実測で 73 文字 3 行が
    /// 14 文字 1 行になった。受け手に渡る前に壊れるので、素通しでは複数行を試験できない。
    /// </summary>
    public static void SendTest(string outputPath, string titlePart, string body)
    {
        if (body.StartsWith('@'))
        {
            try
            {
                body = File.ReadAllText(body[1..]);
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath, $"!! body file unreadable: {ex.Message}",
                    new UTF8Encoding(false));
                return;
            }
        }

        var log = new StringBuilder();
        var desktops = new VirtualDesktopService();
        var scanner = new TabScanner(desktops);
        Guid desktop = AnyDesktop(desktops);

        List<TerminalTab> tabs = scanner.Scan(desktop, (uint)Environment.ProcessId);
        TerminalTab? target = tabs.FirstOrDefault(t => t.Name.Contains(titlePart, StringComparison.Ordinal));

        log.AppendLine($"target filter : {titlePart}");
        log.AppendLine($"tabs found    : {tabs.Count}");
        foreach (TerminalTab tab in tabs)
        {
            string mark = ReferenceEquals(tab, target) ? " <== target" : "";
            log.AppendLine($"  claude={tab.LooksLikeClaudeCode,-5} tabElem={tab.HasTabElement,-5} {tab.Name}{mark}");
        }

        if (target is null)
        {
            log.AppendLine("!! target not found");
            File.WriteAllText(outputPath, log.ToString(), new UTF8Encoding(false));
            return;
        }

        SendResult result = new Sender().Send(target, body);
        SendLog.Write(target, result, body.Length);

        log.AppendLine($"outcome       : {result.Outcome}");
        log.AppendLine($"message       : {result.Message}");

        // 画面に出る注記をここでも出す。診断だけで注記の生成を確かめられるようにするため
        log.AppendLine($"note          : {result.Note ?? "(なし)"}");
        log.AppendLine($"send log      : {SendLog.Path}");

        // 送信ログ（本文の記録）はここでは書かない。診断の合成本文が、読み返すための
        // 記録に混ざると使い物にならなくなる
        log.AppendLine("send history  : (診断では書かない)");

        File.WriteAllText(outputPath, log.ToString(), new UTF8Encoding(false));
    }

    /// <summary>サンプリングでは所属デスクトップを問わない。現在デスクトップの GUID を借りる。</summary>
    private static Guid AnyDesktop(VirtualDesktopService desktops)
    {
        foreach (IntPtr hWnd in NativeMethods.EnumerateVisibleWindows())
        {
            if (!desktops.IsOnCurrentDesktop(hWnd)) continue;
            Guid? id = desktops.GetDesktopId(hWnd);
            if (id is not null) return id.Value;
        }
        return Guid.Empty;
    }

    private static bool IsInteresting(string processName) =>
        processName is "WindowsTerminal" or "powershell" or "pwsh" or "cmd"
                    or "Code" or "Cursor" or "CCPIT" or "explorer" or "chrome";

    private static string SafeProcessName(uint pid)
    {
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "?";
        }
    }
}
