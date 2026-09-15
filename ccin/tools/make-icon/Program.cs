using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MakeIcon;

/// <summary>
/// CCIN のアイコンを作る。**正本は <c>ccin/icon/ccin_icon.svg</c>**。
///
///   make-icon                 svg から png と ico を作り直す（ふだんはこれ）
///   make-icon &lt;画像.png&gt;      png から ico を作る（svg を使わない絵柄にしたとき）
///
/// svg を編集して実行すれば、png も ico も追随する。**書き換えられるのは png と ico だけで、
/// svg には一切書き込まない**（編集した絵が消えないようにするため）。
///
/// svg に解釈できない書き方があると、黙って崩した絵を出さずに止まる。
/// そのときは Inkscape から png を書き出して、png を引数に渡せば ico は作れる。
/// </summary>
internal static class Program
{
    /// <summary>ico に入れる大きさ。小さい側も縮小ではなく、その大きさで描き直す（潰れないため）。</summary>
    private static readonly int[] Sizes = [256, 128, 64, 48, 32, 16];

    private static int Main(string[] args)
    {
        try
        {
            string root = FindRepositoryRoot();
            string svgPath = Path.Combine(root, "ccin", "icon", "ccin_icon.svg");
            string pngPath = Path.Combine(root, "ccin", "icon", "ccin_icon_256.png");
            string icoPath = Path.Combine(root, "ccin", "src", "Ccin", "ccin.ico");

            // 左右対称かを見るだけ。何も書き換えない
            if (args.Length >= 1 && args[0] == "--check")
            {
                string target = args.Length >= 2 ? args[1] : svgPath;
                Console.WriteLine($"確認: {target}（折り返しをかけない素の描画）");
                using Bitmap sample = SvgRenderer.Render(target, 256, applyMirror: false);
                ReportSymmetry(sample);
                return 0;
            }

            var frames = new List<(int Size, byte[] Png)>();

            if (args.Length >= 1)
            {
                string source = args[0];
                if (!File.Exists(source))
                {
                    Console.WriteLine($"元画像が無い: {source}");
                    return 1;
                }

                using var original = new Bitmap(source);
                Console.WriteLine($"元画像: {source}  {original.Width}x{original.Height}");
                foreach (int size in Sizes) frames.Add((size, ToPng(Resize(original, size))));
            }
            else
            {
                if (!File.Exists(svgPath))
                {
                    Console.WriteLine($"svg が無い: {svgPath}");
                    return 1;
                }

                Console.WriteLine($"正本: {svgPath}");
                Console.WriteLine(SvgRenderer.WantsMirror(svgPath)
                    ? "左右対称の指定: あり（data-symmetry=\"mirror\"）。左半分を折り返して仕上げる"
                    : "左右対称の指定: なし。描いたとおりに出す");

                // 対称の確認は 256 の絵で行う。小さい絵は誤差が丸め込まれて差が出ないため
                using (Bitmap largest = SvgRenderer.Render(svgPath, 256))
                {
                    ReportSymmetry(largest);
                    frames.Add((256, ToPng((Bitmap)largest.Clone())));
                }
                foreach (int size in Sizes.Skip(1)) frames.Add((size, ToPng(SvgRenderer.Render(svgPath, size))));

                File.WriteAllBytes(pngPath, frames[0].Png);
                Console.WriteLine($"書いた: {pngPath}");
            }

            WriteIco(icoPath, frames);
            Console.WriteLine($"書いた: {icoPath}  ({Sizes.Length} サイズ / {frames.Sum(f => f.Png.Length):N0} バイト)");
            return 0;
        }
        catch (SvgNotSupportedException e)
        {
            Console.WriteLine("svg を読めなかったので、何も書き換えずに止めた。");
            Console.WriteLine($"  {e.Message}");
            Console.WriteLine();
            Console.WriteLine("Inkscape から 256x256 の png を書き出して、次のように渡せば ico は作れる:");
            Console.WriteLine("  dotnet run --project ccin/tools/make-icon -- ccin/icon/ccin_icon_256.png");
            return 1;
        }
    }

    /// <summary>
    /// 左右対称かを画素で確かめて表示する。
    ///
    /// 「対称に見える」で済ませない。左右を折り返して重ね、色が食い違う画素を数える。
    /// 描画のにじみ（アンチエイリアス）で 1〜2 の差は出るので、それを超えたものだけ数える。
    /// </summary>
    private static void ReportSymmetry(Bitmap bitmap)
    {
        const int Tolerance = 2;   // にじみの差はここまで許す

        int mismatched = 0;
        int worst = 0;
        var worstAt = Point.Empty;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width / 2; x++)
            {
                Color left = bitmap.GetPixel(x, y);
                Color right = bitmap.GetPixel(bitmap.Width - 1 - x, y);

                int delta = Math.Max(
                    Math.Max(Math.Abs(left.R - right.R), Math.Abs(left.G - right.G)),
                    Math.Max(Math.Abs(left.B - right.B), Math.Abs(left.A - right.A)));

                if (delta > worst)
                {
                    worst = delta;
                    worstAt = new Point(x, y);
                }
                if (delta > Tolerance) mismatched++;
            }
        }

        int total = bitmap.Width / 2 * bitmap.Height;
        if (mismatched == 0)
        {
            Console.WriteLine($"左右対称: OK（{total:N0} 対を照合。最大の差 {worst}／許容 {Tolerance}）");
        }
        else
        {
            Console.WriteLine(
                $"左右対称: NG　食い違う画素 {mismatched:N0} / {total:N0} 対。" +
                $"最大の差 {worst} at ({worstAt.X},{worstAt.Y})");
        }
    }

    private static Bitmap Resize(Bitmap source, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        g.DrawImage(source, new Rectangle(0, 0, size, size));
        return bitmap;
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using (bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    /// <summary>
    /// ico を組み立てる。各サイズを PNG のまま収める。
    /// 見出しの幅・高さは 1 バイトなので、256 は 0 で表す決まりになっている。
    /// </summary>
    private static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null) Directory.CreateDirectory(directory);

        using FileStream file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write((ushort)0);              // 予約
        writer.Write((ushort)1);              // 種別: アイコン
        writer.Write((ushort)frames.Count);

        int offset = 6 + (16 * frames.Count);
        foreach ((int size, byte[] png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);            // 色数（0 = 256 色超）
            writer.Write((byte)0);            // 予約
            writer.Write((ushort)1);          // プレーン数
            writer.Write((ushort)32);         // ビット深度
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach ((_, byte[] png) in frames) writer.Write(png);
    }

    /// <summary>ccin フォルダを持つ親を上へ辿って探す。どこから実行しても動くように。</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "ccin", "src", "Ccin")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("ccin/src/Ccin を含むフォルダが見つからない");
    }
}
