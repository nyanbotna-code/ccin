using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;

namespace Ccin.Core;

internal enum ImageSaveOutcome
{
    Saved,
    NoImage,
    ClipboardFailed,
    SaveFailed,
}

internal sealed record ImageSaveResult(ImageSaveOutcome Outcome, string? Path, string Message);

/// <summary>
/// クリップボードの画像をファイルに落とす。
///
/// Claude Code はテキストしか受け取れないので、画像は「保存してパスを渡す」形になる。
/// 保存と貼り付けを手作業でやるのが煩わしい、という要望への対応。
///
/// 取り込み口をクリップボードに限ることで、編集ツールを問わない。ペイントでも他の
/// ツールでも、加工した結果をコピーすれば同じ経路に乗る。専用の連携を作らずに済む。
/// </summary>
internal static class ImageClip
{
    /// <summary>クリップボードは他プロセスにロックされることがあるので数回粘る。</summary>
    private const int ClipboardRetries = 5;
    private const int ClipboardRetryDelayMs = 60;

    /// <summary>同じ秒に 2 回押されたときに上書きしないための連番の上限。</summary>
    private const int MaxNameCollisionSuffix = 99;

    /// <summary>
    /// 保存先。Edit ダイアログから変更でき、<see cref="AppSettings"/> が覚えている。
    /// 参照のたびに引くので、変更が次の保存からすぐ効く。
    /// </summary>
    public static string Folder => AppSettings.ImageFolder;

    /// <summary>
    /// クリップボードの画像を PNG で保存する。成功したらそのフルパスを返す。
    /// </summary>
    public static ImageSaveResult SaveFromClipboard()
    {
        Image? image = TryGetImage();
        if (image is null)
        {
            return new ImageSaveResult(ImageSaveOutcome.NoImage, null,
                "クリップボードに画像がない。Win+Shift+S などで撮ってから押して。");
        }

        try
        {
            Directory.CreateDirectory(Folder);
            string path = NextAvailablePath();
            image.Save(path, ImageFormat.Png);

            return new ImageSaveResult(ImageSaveOutcome.Saved, path,
                $"画像を保存: {path}");
        }
        catch (Exception ex)
        {
            return new ImageSaveResult(ImageSaveOutcome.SaveFailed, null,
                $"画像を保存できなかった: {ex.Message}");
        }
        finally
        {
            image.Dispose();
        }
    }

    /// <summary>
    /// 保存した画像を既定のアプリで開く。エクスプローラーで開いたときと同じ挙動。
    /// 失敗しても保存は済んでいるので、呼び出し側は続行してよい。
    /// </summary>
    public static bool TryOpen(string path)
    {
        try
        {
            // UseShellExecute を立てないと関連付けが効かない
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Image? TryGetImage()
    {
        for (int attempt = 0; attempt < ClipboardRetries; attempt++)
        {
            try
            {
                return Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
            }
            catch
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }
        return null;
    }

    /// <summary>
    /// 日時のファイル名。同じ秒に重なったら連番を足す。
    /// 名前を短く保ちたいので、ミリ秒は衝突したときだけ使わない方針にしている。
    /// </summary>
    private static string NextAvailablePath()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = System.IO.Path.Combine(Folder, $"{stamp}.png");
        if (!File.Exists(path)) return path;

        for (int suffix = 2; suffix <= MaxNameCollisionSuffix; suffix++)
        {
            path = System.IO.Path.Combine(Folder, $"{stamp}_{suffix}.png");
            if (!File.Exists(path)) return path;
        }

        // ここまで埋まるのは異常。最後はミリ秒で逃げる
        return System.IO.Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
    }
}
