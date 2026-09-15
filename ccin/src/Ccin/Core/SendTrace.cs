using System.IO;
using System.Text;

namespace Ccin.Core;

/// <summary>
/// 送信 1 回ぶんの詳細記録。
///
/// 「貼り付けたのに送信されず入力欄に残る」事象は再現しにくい。次に起きたときに
/// 後から原因を判定できるよう、送信のたびに端末の画面と実際のタブ名を残す。
///
/// 判定に使いたいのは 2 点。
/// - 貼り付け後の画面に本文が入っているか（＝貼り付けが届いたか）
/// - Enter 後の画面から本文が消えたか（＝送信として扱われたか）
///
/// 一覧の表示名と送信時点の実名も並べる。アイコンは状態で変わるため、表示が古いまま
/// 送っていたのかどうかがここで判る。
/// </summary>
internal static class SendTrace
{
    /// <summary>1 回の記録で残す画面の文字数。入力欄は末尾にあるので末尾だけでよい。</summary>
    public const int TailChars = 900;

    /// <summary>これを超えたら 1 世代だけ退避する。診断でディスクを埋めない。</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ccin",
        "send-trace.log");

    public static void Write(
        TerminalTab tab,
        string liveTitle,
        string body,
        string selectDetail,
        string pasteDetail,
        string focusBeforeEnter,
        string popupsBeforeEnter,
        string guiThreadBeforeEnter,
        string? beforePaste,
        string? afterPaste,
        string? afterEnter,
        string? afterEnterSettled,
        SendResult result)
    {
        var log = new StringBuilder();
        log.AppendLine("================================================================");
        log.AppendLine($"time        : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        log.AppendLine($"outcome     : {result.Outcome}");
        log.AppendLine($"target      : hwnd=0x{tab.WindowHandle.ToInt64():X} pid={tab.ProcessId} " +
                       $"rid={(tab.RuntimeId.Length > 0 ? tab.RuntimeId : "-")} idx={tab.Index}");
        log.AppendLine($"listed name : {tab.Name}");
        log.AppendLine($"live title  : {liveTitle}");
        log.AppendLine($"name match  : {(tab.Name == liveTitle ? "same" : "DIFFERENT")}");
        log.AppendLine($"single tab  : {tab.IsSingleTabWindow}");
        log.AppendLine($"selection   : {selectDetail}");

        // 貼り付けが届くまでに何が起きたか。確認ダイアログを押したならここに出る。
        // 「Sent なのに送られていない」を後から見分ける決め手になる
        log.AppendLine($"paste       : {pasteDetail}");
        log.AppendLine($"focus@enter : {focusBeforeEnter}");
        log.AppendLine($"popups@enter: {popupsBeforeEnter}");
        log.AppendLine($"gui@enter   : {guiThreadBeforeEnter}");
        log.AppendLine($"body        : {body.Length} chars / {CountLines(body)} line(s)");
        log.AppendLine($"body head   : {Head(body, 120)}");
        log.AppendLine("");
        AppendSnapshot(log, "screen BEFORE paste", beforePaste);
        AppendSnapshot(log, "screen AFTER paste", afterPaste);
        AppendSnapshot(log, "screen AFTER enter (150ms)", afterEnter);

        // 150ms では再描画が間に合わず「変化なし」に見えることが実際にあった。
        // 落ち着いてからの 1 枚と見比べれば、描画待ちと本当の無反応を区別できる
        AppendSnapshot(log, "screen AFTER enter (settled)", afterEnterSettled);

        try
        {
            lock (Gate)
            {
                string? directory = System.IO.Path.GetDirectoryName(Path);
                if (directory is not null) Directory.CreateDirectory(directory);
                Rotate();
                File.AppendAllText(Path, log.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // 診断のために送信を止めない
        }
    }

    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length < MaxBytes) return;

            string previous = Path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(Path, previous);
        }
        catch
        {
            // 退避できなくても記録は続ける
        }
    }

    private static void AppendSnapshot(StringBuilder log, string label, string? text)
    {
        log.AppendLine($"--- {label} ---");
        log.AppendLine(text ?? "(could not read)");
        log.AppendLine("");
    }

    private static int CountLines(string text) => text.Split('\n').Length;

    private static string Head(string text, int max)
    {
        string oneLine = text.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "...";
    }
}
