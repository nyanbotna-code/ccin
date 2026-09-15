namespace Ccin.Core;

/// <summary>
/// 番号を割り当てた送信先。番号はタブが消えるまで動かない。
///
/// <paramref name="DisplayName"/> が null なら実タブ名をそのまま出す。
/// <paramref name="Excluded"/> が true のものは送信先の一覧に出さない（Edit では見える）。
/// </summary>
internal sealed record NumberedTab(int Number, TerminalTab Tab, string? DisplayName, bool Excluded,
    string LogName)
{
    /// <summary>一覧に出す文字列。ユーザーが名前を付けていればそちらを使う。</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Tab.Name : DisplayName;
}

/// <summary>Edit ダイアログが持ち帰る 1 行分。並び順がそのまま番号の順になる。</summary>
internal sealed record TargetEdit(TerminalTab Tab, int Number, string? DisplayName, bool Excluded,
    string? LogName);

/// <summary>
/// 送信先の通し番号・表示名・除外を管理する。
///
/// 番号を走査順（＝ウィンドウの Z オーダー）から作ると、送信のたびに対象が前面化するので
/// 並びが入れ替わる。実際にそれで「一番下＝新しく増えたもの」と思って選んだ先が
/// 別のタブになる事故が起きた。番号は**一度割り当てたらそのタブが消えるまで固定**し、
/// 新しいタブは**常に末尾（今ある最大の番号の次）**に付ける。
///
/// 欠番を埋める案も実装して実測したが、「新しく増えたものが一番下に来る」という
/// 人間側の読みが成り立たなくなる。事故はまさにその読みから起きているので、
/// 番号が飛ぶ代償を払ってでも並びの意味を保つほうを採った。
/// 代わりに Edit ダイアログから明示的に詰め直せるようにしてある（<see cref="Renumber"/>）。
/// 自動で動かないこと・ユーザーが望んだときだけ動くことの両立がここでの狙い。
///
/// 番号空間は仮想デスクトップごとに独立させる。各窓は自分の机の端末しか
/// 一覧に出さないため、机をまたいで通し番号を続けると 1 から始まらなくなる。
///
/// 表示名・番号・除外はいずれも**永続化しない**。再起動後に同じタブを判別する手段が
/// 無いためで、定義書の決定に従う。
/// </summary>
internal sealed class TargetNumbering
{
    private sealed class Entry
    {
        public required int Number { get; set; }
        public required TerminalTab Tab { get; set; }
        public string? DisplayName { get; set; }
        public bool Excluded { get; set; }

        /// <summary>
        /// 送信ログのファイル名。一覧に現れたときに 1 回だけ決め、以後は動かさない。
        ///
        /// タブ名から作らないのは、Claude Code の会話名が変わるたびに同じ送信先の記録が
        /// 別ファイルへ分裂するため。表示名・番号と同じく永続化しないので、CCIN を
        /// 再起動すると新しい名前になる（利用者の要求どおり）。
        /// </summary>
        public required string LogName { get; set; }
    }

    private readonly Dictionary<Guid, List<Entry>> _byDesktop = [];

    /// <summary>
    /// 走査結果に番号を割り当てる。戻り値は番号の昇順で、**除外したものも含む**。
    /// 一覧に出すかどうかは呼び出し側が <see cref="NumberedTab.Excluded"/> で決める。
    ///
    /// 消えたタブの番号はその場で解放するが、**空いた番号は再利用しない**。
    /// 全てのタブが消えると番号は 1 から振り直しになる。
    /// </summary>
    public List<NumberedTab> Assign(Guid desktopId, IReadOnlyList<TerminalTab> tabs)
    {
        List<Entry> entries = EntriesOf(desktopId);

        var consumed = new HashSet<TerminalTab>();   // 参照同一性。同じ走査結果の中で使う
        var present = new List<Entry>();

        // 既存の番号を先に引き当てる。ここで番号が動かないことがこの仕組みの中心
        foreach (Entry entry in entries)
        {
            var remaining = new List<TerminalTab>();
            foreach (TerminalTab tab in tabs)
            {
                if (!consumed.Contains(tab)) remaining.Add(tab);
            }

            TerminalTab? same = TerminalTab.FindSame(remaining, entry.Tab);
            if (same is null) continue;   // 消えた。番号を解放する

            consumed.Add(same);
            entry.Tab = same;             // 会話名が変わっても追従する
            present.Add(entry);
        }

        // 新しいタブは末尾に付ける。番号順に並べるので、常に一覧の一番下に出る
        int next = 0;
        foreach (Entry entry in present) next = Math.Max(next, entry.Number);
        next++;

        foreach (TerminalTab tab in tabs)
        {
            if (consumed.Contains(tab)) continue;
            present.Add(new Entry
            {
                Number = next,
                Tab = tab,
                LogName = FreshLogName(present),
            });
            next++;
        }

        present.Sort((a, b) => a.Number.CompareTo(b.Number));

        entries.Clear();
        entries.AddRange(present);
        return present.ConvertAll(Snapshot);
    }

    /// <summary>今の割り当てをそのまま返す。Edit ダイアログを開くときに使う。</summary>
    public List<NumberedTab> Current(Guid desktopId) =>
        EntriesOf(desktopId).ConvertAll(Snapshot);

    /// <summary>
    /// Edit ダイアログの結果を反映する。渡された行の**順序がそのまま番号順**になる。
    ///
    /// ここに無いタブ（ダイアログを開いている間に消えたもの）は落とす。
    /// 逆に、開いている間に増えたタブは次の <see cref="Assign"/> で末尾に付く。
    /// </summary>
    public void Apply(Guid desktopId, IReadOnlyList<TargetEdit> edits)
    {
        List<Entry> entries = EntriesOf(desktopId);
        var rebuilt = new List<Entry>();

        // ログ名はここで最終的に決める。利用者が打った値をそのまま信じると、
        // 空・使えない文字・他の行との重複がそのままファイル名になる
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (TargetEdit edit in edits)
        {
            string logName = SendHistory.MakeUnique(SendHistory.Normalize(edit.LogName), taken);
            taken.Add(logName);

            rebuilt.Add(new Entry
            {
                Number = edit.Number,
                Tab = edit.Tab,
                DisplayName = string.IsNullOrWhiteSpace(edit.DisplayName) ? null : edit.DisplayName.Trim(),
                Excluded = edit.Excluded,
                LogName = logName,
            });
        }

        rebuilt.Sort((a, b) => a.Number.CompareTo(b.Number));

        entries.Clear();
        entries.AddRange(rebuilt);
    }

    /// <summary>
    /// 番号を 1 から詰め直す。並び順は今の番号順のまま。
    /// 欠番が `Ctrl`+数字 の射程（1〜9）を超えたときの逃げ道。
    /// </summary>
    public void Renumber(Guid desktopId)
    {
        List<Entry> entries = EntriesOf(desktopId);
        entries.Sort((a, b) => a.Number.CompareTo(b.Number));

        int number = 1;
        foreach (Entry entry in entries)
        {
            entry.Number = number;
            number++;
        }
    }

    /// <summary>一覧に出している名前。ユーザーが付けた名前があればそれ、無ければ実タブ名。</summary>
    public string LabelOf(Guid desktopId, TerminalTab tab)
    {
        foreach (Entry entry in EntriesOf(desktopId))
        {
            if (TerminalTab.FindSame([entry.Tab], tab) is null) continue;
            return string.IsNullOrWhiteSpace(entry.DisplayName) ? tab.Name : entry.DisplayName;
        }
        return tab.Name;
    }

    /// <summary>
    /// 送信ログ用に、番号とユーザーが付けた名前を取り出す。割り当てが無ければ番号 0・名前なし。
    ///
    /// <see cref="LabelOf"/> と分けているのは、ログでは「ユーザーが付けた名前」と「実タブ名」を
    /// 区別して残す必要があるため。ログのファイル名は表示名で決まるので、後からファイルが
    /// 分裂したときに、どちらの名前で分かれたのかを追える必要がある。
    /// </summary>
    public (int Number, string? DisplayName, string LogName) DescribeFor(Guid desktopId, TerminalTab tab)
    {
        foreach (Entry entry in EntriesOf(desktopId))
        {
            if (TerminalTab.FindSame([entry.Tab], tab) is null) continue;
            return (entry.Number,
                string.IsNullOrWhiteSpace(entry.DisplayName) ? null : entry.DisplayName,
                entry.LogName);
        }

        // 一覧に無い送信先へ送ることは通常ないが、記録を捨てるよりは既定名で残す
        return (0, null, SendHistory.DefaultName());
    }

    /// <summary>その番号のタブ。無ければ null。`Ctrl`+数字 で引く。</summary>
    public TerminalTab? ByNumber(Guid desktopId, int number)
    {
        foreach (Entry entry in EntriesOf(desktopId))
        {
            if (entry.Number == number && !entry.Excluded) return entry.Tab;
        }
        return null;
    }

    /// <summary>
    /// このタブが除外されているか。To Active の送信先を弾くのに使う。
    ///
    /// 除外したものへは To Active でも送らない。一覧から消えているのに
    /// 最前面というだけで送信先になるのでは、除外した意味がない。
    /// </summary>
    public bool IsExcluded(Guid desktopId, TerminalTab tab)
    {
        foreach (Entry entry in EntriesOf(desktopId))
        {
            if (entry.Excluded && TerminalTab.FindSame([entry.Tab], tab) is not null) return true;
        }
        return false;
    }

    private List<Entry> EntriesOf(Guid desktopId)
    {
        if (!_byDesktop.TryGetValue(desktopId, out List<Entry>? entries))
        {
            entries = [];
            _byDesktop[desktopId] = entries;
        }
        return entries;
    }

    private static NumberedTab Snapshot(Entry entry) =>
        new(entry.Number, entry.Tab, entry.DisplayName, entry.Excluded, entry.LogName);

    /// <summary>
    /// まだ使われていないログ名を作る。秒単位の既定名は、同じ走査で 2 件増えると衝突する。
    /// </summary>
    private static string FreshLogName(IEnumerable<Entry> existing)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Entry entry in existing) taken.Add(entry.LogName);
        return SendHistory.MakeUnique(SendHistory.DefaultName(), taken);
    }
}
