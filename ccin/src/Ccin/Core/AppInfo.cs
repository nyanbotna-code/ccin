using System.Reflection;

namespace Ccin.Core;

/// <summary>
/// アプリ自身の名前と版。
///
/// 版を手で書いた定数にすると、`Ccin.csproj` の値と必ず食い違う。実行ファイルに
/// 埋まっている値をそのまま読むので、書く場所は csproj の 1 か所だけで済む。
/// </summary>
internal static class AppInfo
{
    /// <summary>窓のタイトルの先頭。診断（--windows）が自分の窓を見分けるのに使う。</summary>
    public const string TitlePrefix = "CCIN  -";

    /// <summary>
    /// 表示用の版。`1.0.5.0` の末尾は使わないので落とす。
    ///
    /// 取れなかったときに例外で落とすと、版を出すためにアプリが起動しないことになる。
    /// 表示の都合でしかないので、読めなければ空にして黙って続ける。
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>メイン画面のタイトル。版は末尾に置く（先頭は診断の目印なので動かさない）。</summary>
    public static string WindowTitle =>
        Version.Length > 0
            ? $"{TitlePrefix}  Claude Code 入力エディタ  v{Version}"
            : $"{TitlePrefix}  Claude Code 入力エディタ";

    private static string ReadVersion()
    {
        try
        {
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return string.Empty;
        }
    }
}
