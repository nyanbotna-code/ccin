using System.IO;
using System.Text.Json;

namespace Ccin.Core;

/// <summary>
/// ファイルに落とす設定の中身。増えるたびにここへ足す。
///
/// 項目を足しても古いファイルは読める（欠けた項目は null になる）ので、
/// 既存の設定を作り直させる必要はない。
/// </summary>
internal sealed record AppSettingsData(
    string? ImageFolder, int? EditWidth, int? EditHeight, int? EditX, int? EditY,
    string? ColorMode = null, string? LogFolder = null,
    bool? FadeWhenInactive = null, int? InactiveOpacity = null, bool? AlwaysOnTop = null,
    bool? PreviewImage = null);

/// <summary>
/// 再起動をまたいで覚えておく設定。
///
/// 表示名・番号・除外は永続化しない。再起動後に「同じタブ」を判別する手段が無いため、
/// 無理に覚えると別のセッションへ他人の名前が付く（定義書の決定）。
/// ここに置くのは、タブの同一性と無関係なものだけ。今はワークフォルダだけ。
///
/// 窓の位置は <see cref="WindowPlacement"/> が別ファイルで持っている。用途が違い、
/// 保存の頻度も違う（窓は閉じるたび、こちらは変更したときだけ）ので分けたままにする。
/// </summary>
internal static class AppSettings
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ccin",
        "settings.json");

    /// <summary>既定のワークフォルダ。設定が無いとき・空のときはここへ落とす。</summary>
    public static string DefaultImageFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ccin",
        "images");

    /// <summary>
    /// 既定の送信ログの保存先。
    ///
    /// 画像と分けるのは、画像は本文に貼るための材料で、ログは読み返すための記録という
    /// 用途の違いがあるため。既定を同じ親フォルダに置いておけば、まとめて捨てられる。
    /// </summary>
    public static string DefaultLogFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ccin",
        "log");

    /// <summary>これ未満の値は壊れた設定とみなして捨てる。</summary>
    private const int MinimumEditSide = 200;

    private static string _imageFolder = DefaultImageFolder;
    private static string _logFolder = DefaultLogFolder;
    private static int _inactiveOpacity = 70;
    private static int _editWidth;
    private static int _editHeight;

    static AppSettings() => Load();

    /// <summary>
    /// ダークで描くか。既定は true で、画面に切り替えは置かない（要求はダークモード）。
    ///
    /// 設定ファイルに <c>"ColorMode": "light"</c> と書いたときだけ、変更前の見た目へ戻る。
    /// 逃げ道を残すのは、配色が原因で読めない事態になったときに、実行ファイルを
    /// 差し戻さずに復旧できるようにするため。書き込む UI は用意しない。
    /// </summary>
    public static bool DarkMode { get; private set; } = true;

    /// <summary>
    /// Edit ダイアログの大きさ。覚えていなければ null。
    /// 値は物理ピクセル（メイン画面の <see cref="WindowPlacement"/> と同じ単位）。
    /// </summary>
    public static Size? EditSize
    {
        get => _editWidth >= MinimumEditSide && _editHeight >= MinimumEditSide
            ? new Size(_editWidth, _editHeight)
            : null;
        set
        {
            if (value is null || value.Value.Width < MinimumEditSide || value.Value.Height < MinimumEditSide)
            {
                _editWidth = 0;
                _editHeight = 0;
                return;
            }
            _editWidth = value.Value.Width;
            _editHeight = value.Value.Height;
        }
    }

    /// <summary>
    /// Edit ダイアログの位置。覚えていなければ null。物理ピクセルの画面座標。
    ///
    /// 位置を覚えると、モニタを外したときに画面の外の位置が残る。復元する側で
    /// 画面内に入るかを確かめ、入らなければ既定（CCIN のいるモニタの中央）へ倒す。
    /// </summary>
    public static Point? EditLocation { get; set; }

    /// <summary>画像の保存先。空の値は受け付けず、既定へ戻す。</summary>
    public static string ImageFolder
    {
        get => _imageFolder;
        set => _imageFolder = string.IsNullOrWhiteSpace(value) ? DefaultImageFolder : value.Trim();
    }

    /// <summary>送信ログの保存先。空の値は受け付けず、既定へ戻す。</summary>
    public static string LogFolder
    {
        get => _logFolder;
        set => _logFolder = string.IsNullOrWhiteSpace(value) ? DefaultLogFolder : value.Trim();
    }

    /// <summary>非アクティブのときに窓を薄くするか。既定は切（今までの見え方を変えない）。</summary>
    public static bool FadeWhenInactive { get; set; }

    /// <summary>
    /// 非アクティブのときの不透明度（パーセント）。
    ///
    /// 下限を <see cref="MinimumOpacity"/> で切るのは、0 に近づけると窓が見えなくなり、
    /// 設定を戻す操作そのものができなくなるため。掴めない窓を作らない。
    /// </summary>
    public static int InactiveOpacity
    {
        get => _inactiveOpacity;
        set => _inactiveOpacity = Math.Clamp(value, MinimumOpacity, 100);
    }

    /// <summary>常に最前面に出すか。既定は入（今までの振る舞い）。</summary>
    public static bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// 画像を貼ったあと、既定のアプリで開いて中身を見せるか。既定は入（今までの振る舞い）。
    ///
    /// 開くのは「何を貼ったか」を目で確かめるためだが、連続で貼るときはビューアが
    /// そのたびに前へ出て邪魔になる。切れるようにする。
    /// </summary>
    public static bool PreviewImage { get; set; } = true;

    /// <summary>これ以上は薄くしない。事実上見えなくなる領域を避ける。</summary>
    public const int MinimumOpacity = 20;

    private static void Load()
    {
        try
        {
            if (!File.Exists(Path)) return;
            AppSettingsData? data = JsonSerializer.Deserialize<AppSettingsData>(File.ReadAllText(Path));
            if (data is null) return;
            ImageFolder = data.ImageFolder ?? DefaultImageFolder;
            LogFolder = data.LogFolder ?? DefaultLogFolder;

            FadeWhenInactive = data.FadeWhenInactive ?? false;
            InactiveOpacity = data.InactiveOpacity ?? 70;
            AlwaysOnTop = data.AlwaysOnTop ?? true;   // 記録が無いときは今までの振る舞い
            PreviewImage = data.PreviewImage ?? true;

            // 明示的に light と書いてあるときだけ戻す。値が無い・読めないときはダーク
            DarkMode = !string.Equals(data.ColorMode, "light", StringComparison.OrdinalIgnoreCase);

            if (data.EditWidth is int width && data.EditHeight is int height)
            {
                EditSize = new Size(width, height);
            }

            if (data.EditX is int x && data.EditY is int y)
            {
                EditLocation = new Point(x, y);
            }
        }
        catch
        {
            // 壊れていたら既定で動かす。設定の読み込みで起動を止めない
        }
    }

    public static void Save()
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is not null) Directory.CreateDirectory(directory);
            var data = new AppSettingsData(
                _imageFolder,
                _editWidth > 0 ? _editWidth : null,
                _editHeight > 0 ? _editHeight : null,
                EditLocation?.X,
                EditLocation?.Y,
                DarkMode ? null : "light",   // 既定（ダーク）のときは書かない
                _logFolder,
                FadeWhenInactive,
                _inactiveOpacity,
                AlwaysOnTop,
                PreviewImage);
            File.WriteAllText(Path, JsonSerializer.Serialize(data));
        }
        catch
        {
            // 保存できなくても実害は「次回に覚えていない」だけ
        }
    }
}
