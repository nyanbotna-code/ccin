using System.IO;
using System.Reflection;

namespace Ccin.Ui;

/// <summary>
/// 窓に出すアイコン。
///
/// csproj の <c>ApplicationIcon</c> は**実行ファイルのリソース**を差し替えるだけで、
/// .NET の WinForms はそれを窓のアイコンには使わない（既定は WinForms 内蔵のもの）。
/// エクスプローラーでは新しい絵柄なのにタイトルバーだけ既定のまま、という食い違いになる。
/// 同じ ico を埋め込みリソースとしても持ち、窓へは自分で当てる。
///
/// 複数サイズを収めた ico をそのまま渡すので、タイトルバー用の小さい絵と
/// Alt+Tab 用の大きい絵は Windows が選ぶ。
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "ccin.ico";

    private static readonly Lazy<Icon?> Cached = new(Load);

    /// <summary>読めなければ null。アイコンが無いことで起動を止めない。</summary>
    public static Icon? Value => Cached.Value;

    private static Icon? Load()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            // 埋め込み忘れ・壊れた ico でも既定のアイコンで動けばよい
            return null;
        }
    }

    public static void Apply(Form form)
    {
        if (Value is Icon icon) form.Icon = icon;
    }
}
