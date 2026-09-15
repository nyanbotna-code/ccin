using System.IO;
using System.Text;

namespace Ccin.Core;

/// <summary>
/// 送った本文を人が読み返すための記録。送信先ごとに 1 ファイルへ追記する。
///
/// <see cref="SendLog"/> とは目的が違う。あちらは「どのタブへ送ったか」を機械的に追うための
/// 1 行 1 送信の記録で、本文を持たない。こちらは本文を全文残し、利用者がエディタで開いて
/// 読み返す・使い回すためのもの。だから書式も人間可読を優先する。
///
/// 読み戻す機能は持たない（要求として見送り）。機械で解析し直さないので、本文の中に
/// 区切り線と同じ文字列が入っていても実害がない。本文は一切加工せず原文のまま書く。
///
/// ファイル名は送信先ごとに <see cref="TargetNumbering"/> が持つ。以前はタブ名から
/// 作っていたが、Claude Code のタブ名は会話名で刻々変わるため、同じ送信先の記録が
/// 名前の変化で別ファイルへ分裂していた。名前を決めるのは一覧に現れた 1 回きりにする。
///
/// 書き込みの失敗は握り潰す。記録のために送信機能を止めるのは本末転倒。
/// </summary>
internal static class SendHistory
{
    private static readonly object Gate = new();

    /// <summary>ファイル名に使えない文字の置き換え先。</summary>
    private const char Replacement = '_';

    /// <summary>自動で付ける名前の拡張子。</summary>
    public const string Extension = ".log";

    /// <summary>
    /// ファイル名に使う長さの上限。パス全体で 260 文字の制限があるため、
    /// 保存先フォルダが深い場合に備えて名前側を短く抑える。
    /// </summary>
    private const int MaxNameLength = 60;

    /// <summary>
    /// 送信先が一覧に現れたときに付ける既定の名前。
    ///
    /// タブ名を使わないのは、会話名が変わるたびに記録が分裂するため。現れた時刻なら
    /// CCIN が動いている間は変わらない。
    /// </summary>
    public static string DefaultName() =>
        DateTime.Now.ToString("yyyyMMdd_HHmmss") + Extension;

    /// <summary>
    /// 利用者が打った名前をファイル名として使える形に整える。空なら既定の名前へ倒す。
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DefaultName();

        var buffer = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            buffer.Append(Path.GetInvalidFileNameChars().Contains(c) ? Replacement : c);
        }

        // 末尾の . と空白は Windows が落とす。残したままだと意図しない名前になる
        string result = buffer.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > MaxNameLength) result = result[..MaxNameLength].TrimEnd('.', ' ');

        return result.Length == 0 ? DefaultName() : result;
    }

    /// <summary>
    /// すでに使われている名前と重ならないようにする。重なったら拡張子の前に <c>_2</c> から付ける。
    /// </summary>
    public static string MakeUnique(string name, ISet<string> taken)
    {
        if (!taken.Contains(name)) return name;

        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);

        for (int n = 2; n < 1000; n++)
        {
            string candidate = $"{stem}_{n}{ext}";
            if (!taken.Contains(candidate)) return candidate;
        }

        // ここまで来るのは異常だが、上書きするよりは重複を許す方がまし
        return name;
    }

    /// <summary>そのログの置き場所。まだ書かれていなければ存在しない。</summary>
    public static string PathOf(string logName) =>
        Path.Combine(AppSettings.LogFolder, logName);

    /// <summary>
    /// 1 送信を追記する。
    ///
    /// 見出しの次の行に No・表示名・タブ名・ウィンドウハンドル・RuntimeId を残す。
    /// 利用者が途中でファイル名を変えても、どの送信先の記録かを後から辿れるようにするため。
    /// </summary>
    public static void Write(TerminalTab tab, int number, string? displayName, string logName,
        SendResult result, string body)
    {
        try
        {
            string folder = AppSettings.LogFolder;
            string path = Path.Combine(folder, Normalize(logName));

            var text = new StringBuilder();
            text.Append("===== ")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("  ")
                .Append(result.Outcome.ToString());

            if (!string.IsNullOrEmpty(result.Note))
            {
                text.Append("  [").Append(result.Note).Append(']');
            }
            text.AppendLine(" =====");

            text.Append("送信先: ");
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                text.Append(displayName).Append(" [").Append(tab.Name).Append(']');
            }
            else
            {
                text.Append(tab.Name);
            }
            text.Append("  (No.").Append(number)
                .Append("  hwnd=0x").Append(tab.WindowHandle.ToInt64().ToString("X"))
                .Append("  rid=").Append(tab.RuntimeId.Length > 0 ? tab.RuntimeId : "-")
                .AppendLine(")");

            // 本文は原文のまま。行頭に印を付けると、読むときにエディタの折り返しで邪魔になる
            text.AppendLine(body);
            text.AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(folder);

                // 新規作成のときだけ BOM が付く。エディタで開いたときの文字化けを防ぐ
                File.AppendAllText(path, text.ToString(), new UTF8Encoding(true));
            }
        }
        catch
        {
            // ディスク不調・権限・排他・不正なパス。いずれも送信の成否とは無関係
        }
    }
}
