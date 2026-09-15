namespace Ccin.Core;

/// <summary>
/// 端末ウィンドウの中の 1 タブ。CCIN の送信先はウィンドウではなくこの単位。
///
/// Windows Terminal のウィンドウタイトルはアクティブなタブのものしか返さないため、
/// ウィンドウ単位で見ると背面タブの Claude Code が存在ごと見えなくなる。
/// </summary>
internal sealed class TerminalTab
{
    public required IntPtr WindowHandle { get; init; }
    public required uint ProcessId { get; init; }
    public required string ProcessName { get; init; }

    /// <summary>ウィンドウ内での位置。タブを並べ替えると変わる。</summary>
    public required int Index { get; init; }

    /// <summary>タブ名。Claude Code では会話名になり、会話が進むと変わる。</summary>
    public required string Name { get; init; }

    public required bool IsSelected { get; init; }

    /// <summary>
    /// UI Automation が要素に振る識別子。同一プロセス内では安定するが、
    /// 別ウィンドウへドラッグ移動すると変わると見られるため、単独では信頼しない。
    /// </summary>
    public required string RuntimeId { get; init; }

    public required Guid DesktopId { get; init; }

    /// <summary>ウィンドウが 1 タブしか持たない（タブバーが出ない）場合 true。</summary>
    public required bool IsSingleTabWindow { get; init; }

    /// <summary>
    /// UI Automation のタブ要素として取れたか。
    /// false は「タブの概念を持たないウィンドウを 1 タブとみなした」ことを意味する。
    /// PowerShell スクリプトが作った WinForms ウィンドウもここに落ちるため、
    /// 端末かどうかの判定にはこの区別が要る。
    /// </summary>
    public required bool HasTabElement { get; init; }

    /// <summary>
    /// Claude Code のタブか。
    ///
    /// 定義書は先頭記号 U+23FA の固定一致を前提にしていたが、実測でこれが崩れた。
    /// 稼働中は U+25D1、待機中は U+2733 と、状態によって記号が変わる。
    /// 記号を列挙すると Claude Code 側の変更で黙って検出漏れするため、
    /// 「非 ASCII の記号 1 文字 + 空白」という**形**で判定する。
    ///
    /// あわせてタブ要素であることを必須にする。これが無いと、タイトルに
    /// "Claude Code" を含むだけの別アプリのウィンドウ（現行 ccinput 等）を拾う。
    /// </summary>
    public bool LooksLikeClaudeCode =>
        HasTabElement &&
        (HasSymbolPrefix(Name) || Name.Contains("Claude Code", StringComparison.OrdinalIgnoreCase));

    /// <summary>タイトルが「非 ASCII の記号 1 文字 + 空白」で始まるか。</summary>
    private static bool HasSymbolPrefix(string name)
    {
        if (name.Length < 2) return false;

        // ConvertToUtf32 は孤立サロゲートで例外を投げる。ここは ASCII かどうかを見たいだけで、
        // サロゲートは必ず 0x7F を超えるので、先頭 1 文字の比較で足りる
        if (name[0] <= 0x7F) return false;     // ASCII 始まりは通常のシェルタイトル

        var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(name, 0);
        bool isSymbol = category is System.Globalization.UnicodeCategory.OtherSymbol
                                 or System.Globalization.UnicodeCategory.MathSymbol
                                 or System.Globalization.UnicodeCategory.ModifierSymbol
                                 or System.Globalization.UnicodeCategory.OtherPunctuation;
        if (!isSymbol) return false;

        // 記号の直後は必ず空白。日本語タイトルの誤検出を防ぐ
        int next = char.IsSurrogatePair(name, 0) ? 2 : 1;
        return next < name.Length && name[next] == ' ';
    }

    /// <summary>
    /// 送信先として今も生きているか。
    ///
    /// ウィンドウハンドルは閉じたウィンドウのものが後から別のウィンドウへ再利用される。
    /// <c>IsWindow</c> は再利用されたハンドルにも true を返すので、プロセス ID まで見る。
    /// 送信も前面化もこの判定を通す。
    /// </summary>
    public bool IsAlive =>
        NativeMethods.IsWindow(WindowHandle) &&
        NativeMethods.GetProcessId(WindowHandle) == ProcessId;

    /// <summary>
    /// 一覧を取り直したときに「さっきのあれ」を選び直すための同定。
    /// RuntimeId → ウィンドウ + タブ名 → ウィンドウ + 位置 の順に落とす。
    ///
    /// 同じ優先順を <see cref="TabScanner.FindMatchingTab"/> でも使う。
    /// あちらは UI Automation の要素が相手で型が違うため共通化していない。
    /// 順序を変えるときは両方を直すこと。
    ///
    /// 見つからなくても致命傷にしない。最悪の結果は「選択が外れる」に留める。
    /// </summary>
    public static TerminalTab? FindSame(IEnumerable<TerminalTab> candidates, TerminalTab target)
    {
        var list = candidates as IList<TerminalTab> ?? candidates.ToList();

        if (target.RuntimeId.Length > 0)
        {
            foreach (TerminalTab tab in list)
            {
                if (tab.RuntimeId == target.RuntimeId) return tab;
            }
        }

        foreach (TerminalTab tab in list)
        {
            if (tab.WindowHandle == target.WindowHandle && tab.Name == target.Name) return tab;
        }

        foreach (TerminalTab tab in list)
        {
            if (tab.WindowHandle == target.WindowHandle && tab.Index == target.Index) return tab;
        }

        return null;
    }

    public override string ToString() => $"{ProcessName} [{Index}] {Name}";
}
