using System.IO;
using System.Text;

namespace Ccin.Core;

/// <summary>
/// 送信の記録。1 送信 1 行を追記する。
///
/// 現行 ccinput の運用中に「ステータスバーの送信先とドロップダウンの選択が食い違う」
/// 事象が観測され、原因の特定に至らなかった。表示だけを見ていては対象の同一性を
/// 事後に追えないため、送信のたびにタブの識別子（ウィンドウハンドル / PID / RuntimeId）を残す。
///
/// 書き込みの失敗は握り潰す。ログのために送信機能を止めるのは本末転倒。
/// </summary>
internal static class SendLog
{
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ccin",
        "send.log");

    public static void Write(TerminalTab tab, SendResult result, int bodyLength)
    {
        string line = string.Join('\t',
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            result.Outcome.ToString(),
            $"hwnd=0x{tab.WindowHandle.ToInt64():X}",
            $"pid={tab.ProcessId}",
            $"rid={(tab.RuntimeId.Length > 0 ? tab.RuntimeId : "-")}",
            $"len={bodyLength}",
            $"name={tab.Name}");

        try
        {
            lock (Gate)
            {
                string? directory = System.IO.Path.GetDirectoryName(Path);
                if (directory is not null) Directory.CreateDirectory(directory);
                File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // ディスク不調・権限・排他。いずれも送信の成否とは無関係
        }
    }
}
