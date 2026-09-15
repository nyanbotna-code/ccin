using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Xml.Linq;

namespace MakeIcon;

/// <summary>読めない書き方に出会ったとき。黙って崩した絵を出さないための例外。</summary>
internal sealed class SvgNotSupportedException(string message) : Exception(message);

/// <summary>
/// アイコン用の最小限の SVG 描画。
///
/// **汎用の SVG 描画ではない。** `ccin_icon.svg` が使っている書き方だけを解釈し、
/// それ以外に出会ったら <see cref="SvgNotSupportedException"/> で止まる。
/// 黙って無視すると「絵が欠けた ico」が出来上がり、しかもビルドは通ってしまうため。
///
/// 対応: rect / circle / ellipse / path（m M l L h H v V c C s S z Z）/ g / mask
/// 非対応: transform, gradient, stroke, text, image, use, clip, opacity
///
/// 止まったときは、Inkscape から png を書き出して
/// <c>make-icon &lt;その png&gt;</c> を使えば ico は作れる。
/// </summary>
internal static class SvgRenderer
{
    /// <summary>解釈しないが、絵に影響しないので黙って飛ばしてよい要素。</summary>
    private static readonly HashSet<string> Ignorable =
        ["defs", "metadata", "title", "desc", "namedview", "mask"];

    /// <summary>描いている途中で持ち回るもの。層を作るのに大きさと倍率が要る。</summary>
    private sealed record Canvas(int Size, double ScaleX, double ScaleY, Dictionary<string, XElement> Masks)
    {
        public Bitmap NewLayer() =>
            new(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        /// <summary>層に描くための Graphics。倍率と描画品質を本体と揃える。</summary>
        public Graphics Begin(Bitmap layer)
        {
            Graphics g = Graphics.FromImage(layer);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // HighQuality は「画素 i が座標 [i, i+1) を覆う」扱い。
            // Default だと画素の中心が整数座標になり、左右対称に作った図形が 1 画素ずれて出る（実測）
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform((float)ScaleX, (float)ScaleY);
            return g;
        }
    }

    /// <param name="applyMirror">
    /// false にすると、絵が <c>data-symmetry="mirror"</c> を宣言していても折り返さない。
    /// 描画そのものの左右差を測るときに使う。
    /// </param>
    public static Bitmap Render(string path, int size, bool applyMirror = true)
    {
        XDocument document = XDocument.Load(path);
        XElement root = document.Root ?? throw new SvgNotSupportedException("svg の中身が空");

        (double width, double height) = ViewBox(root);
        var canvas = new Canvas(size, size / width, size / height, CollectMasks(document));

        Bitmap bitmap = canvas.NewLayer();
        using (Graphics g = canvas.Begin(bitmap))
        {
            DrawChildren(root, g, canvas);
        }

        if (applyMirror && WantsMirror(root)) MirrorLeftHalf(bitmap);
        return bitmap;
    }

    /// <summary>
    /// 左右対称に仕上げてよいかを、絵の側の宣言で決める。
    /// ルートの <c>data-symmetry="mirror"</c> があるときだけ折り返す。
    ///
    /// 既定で折り返さないのは、わざと非対称に描いた絵を黙って壊さないため。
    /// </summary>
    public static bool WantsMirror(XElement root) =>
        string.Equals((string?)root.Attribute("data-symmetry"), "mirror", StringComparison.OrdinalIgnoreCase);

    public static bool WantsMirror(string path) =>
        XDocument.Load(path).Root is XElement root && WantsMirror(root);

    /// <summary>
    /// 左半分を右半分へ折り返す。
    ///
    /// GDI+ の曲線の描画は左右で微妙に食い違う（中心に置いた円だけでも 264 画素ずれるのを実測）。
    /// 幾何が対称でも描画結果は対称にならないので、最後に折り返して揃える。
    /// **幾何のズレを隠す処理ではない**（そちらは svg の座標で直すこと）。
    /// </summary>
    private static void MirrorLeftHalf(Bitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width / 2; x++)
            {
                bitmap.SetPixel(bitmap.Width - 1 - x, y, bitmap.GetPixel(x, y));
            }
        }
    }

    private static Dictionary<string, XElement> CollectMasks(XDocument document)
    {
        var masks = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement mask in document.Descendants().Where(e => e.Name.LocalName == "mask"))
        {
            string? id = (string?)mask.Attribute("id");
            if (id is null) throw new SvgNotSupportedException("mask に id が無い");

            foreach (string units in (string[])["maskUnits", "maskContentUnits"])
            {
                string? value = (string?)mask.Attribute(units);
                if (value is not null && value != "userSpaceOnUse")
                {
                    throw new SvgNotSupportedException(
                        $"mask id=\"{id}\" の {units}=\"{value}\" は非対応。userSpaceOnUse だけ扱える");
                }
            }

            masks[id] = mask;
        }
        return masks;
    }

    private static (double Width, double Height) ViewBox(XElement root)
    {
        string? box = (string?)root.Attribute("viewBox");
        if (box is null) throw new SvgNotSupportedException("viewBox が無い");

        double[] parts = Numbers(box);
        if (parts.Length != 4) throw new SvgNotSupportedException($"viewBox の値が 4 つでない: {box}");
        if (parts[0] != 0 || parts[1] != 0)
        {
            throw new SvgNotSupportedException($"viewBox の原点が 0 0 でない: {box}");
        }
        return (parts[2], parts[3]);
    }

    private static void DrawChildren(XElement parent, Graphics g, Canvas canvas)
    {
        foreach (XElement element in parent.Elements())
        {
            string name = element.Name.LocalName;
            if (Ignorable.Contains(name)) continue;

            if (element.Attribute("transform") is not null)
            {
                throw new SvgNotSupportedException(
                    $"<{name} id=\"{(string?)element.Attribute("id")}\"> に transform が付いている。" +
                    "Inkscape で図形を動かすとこうなる。[パス] → [オブジェクトをパスへ] で座標へ焼き込むか、png 経由で作ること");
            }

            string? maskReference = (string?)element.Attribute("mask");
            if (maskReference is not null)
            {
                if (name != "g")
                {
                    throw new SvgNotSupportedException(
                        $"<{name}> に直接 mask が付いている。mask は <g> にだけ付けられる");
                }
                DrawMaskedGroup(element, maskReference, g, canvas);
                continue;
            }

            if (name == "g")
            {
                DrawChildren(element, g, canvas);
                continue;
            }

            Style style = Style.Of(element);
            if (style.Hidden) continue;

            using GraphicsPath? shape = Shape(element);
            if (shape is null)
            {
                throw new SvgNotSupportedException(
                    $"<{name}> は解釈できない。対応しているのは rect / circle / ellipse / path / g だけ");
            }

            if (style.Stroke is not null)
            {
                throw new SvgNotSupportedException(
                    $"<{name} id=\"{(string?)element.Attribute("id")}\"> に線（stroke）が指定されている。" +
                    "塗りだけで作ること（線はパスに変換すると同じ見た目になる）");
            }

            shape.FillMode = style.EvenOdd ? FillMode.Alternate : FillMode.Winding;
            using var brush = new SolidBrush(style.Fill);
            g.FillPath(brush, shape);
        }
    }

    /// <summary>
    /// mask 付きの塊を描く。
    ///
    /// 中身を別の層へ描き、mask を別の層へ描いてから、mask の明るさで中身の透明度を削る。
    /// mask が黒い場所は中身が消える＝**穴が開く**。白い図形を色抜きにするのに使っている。
    /// </summary>
    private static void DrawMaskedGroup(XElement group, string reference, Graphics g, Canvas canvas)
    {
        string id = MaskId(reference);
        if (!canvas.Masks.TryGetValue(id, out XElement? mask))
        {
            throw new SvgNotSupportedException($"mask=\"{reference}\" の参照先 id=\"{id}\" が見つからない");
        }

        using Bitmap content = canvas.NewLayer();
        using (Graphics layer = canvas.Begin(content))
        {
            DrawChildren(group, layer, canvas);
        }

        using Bitmap coverage = canvas.NewLayer();
        using (Graphics layer = canvas.Begin(coverage))
        {
            DrawChildren(mask, layer, canvas);
        }

        ApplyMask(content, coverage);

        // 層は既に実寸で描けているので、貼るときは倍率を外す
        Matrix saved = g.Transform;
        g.ResetTransform();
        g.DrawImageUnscaled(content, 0, 0);
        g.Transform = saved;
    }

    private static string MaskId(string reference)
    {
        string value = reference.Trim();
        if (!value.StartsWith("url(", StringComparison.Ordinal) || !value.EndsWith(')'))
        {
            throw new SvgNotSupportedException($"mask=\"{reference}\" は url(#id) の形で書くこと");
        }
        return value[4..^1].Trim().TrimStart('#');
    }

    /// <summary>mask の明るさ（と不透明度）で、中身の不透明度を削る。</summary>
    private static void ApplyMask(Bitmap content, Bitmap coverage)
    {
        for (int y = 0; y < content.Height; y++)
        {
            for (int x = 0; x < content.Width; x++)
            {
                Color source = content.GetPixel(x, y);
                if (source.A == 0) continue;

                Color m = coverage.GetPixel(x, y);

                // SVG の決まりどおり「明るさ × 不透明度」を通す割合とする
                double luminance = ((0.2126 * m.R) + (0.7152 * m.G) + (0.0722 * m.B)) / 255.0;
                double keep = luminance * (m.A / 255.0);

                int alpha = (int)Math.Round(source.A * keep);
                content.SetPixel(x, y, Color.FromArgb(Math.Clamp(alpha, 0, 255), source.R, source.G, source.B));
            }
        }
    }

    private static GraphicsPath? Shape(XElement element) => element.Name.LocalName switch
    {
        "rect" => Rect(element),
        "circle" => Circle(element),
        "ellipse" => Ellipse(element),
        "path" => PathData(element),
        _ => null,
    };

    private static GraphicsPath Rect(XElement element)
    {
        double x = Value(element, "x") ?? 0;
        double y = Value(element, "y") ?? 0;
        double w = Value(element, "width") ?? throw new SvgNotSupportedException("rect に width が無い");
        double h = Value(element, "height") ?? throw new SvgNotSupportedException("rect に height が無い");

        // SVG の決まり: 片方だけ指定されていればもう片方も同じ値。
        // 両方指定されていて片方が 0 なら角は丸くならない
        double? rxAttribute = Value(element, "rx");
        double? ryAttribute = Value(element, "ry");
        double rx = rxAttribute ?? ryAttribute ?? 0;
        double ry = ryAttribute ?? rxAttribute ?? 0;
        rx = Math.Min(rx, w / 2);
        ry = Math.Min(ry, h / 2);

        var path = new GraphicsPath();
        if (rx <= 0 || ry <= 0)
        {
            path.AddRectangle(new RectangleF((float)x, (float)y, (float)w, (float)h));
            return path;
        }

        float dx = (float)(rx * 2);
        float dy = (float)(ry * 2);
        var r = new RectangleF((float)x, (float)y, (float)w, (float)h);
        path.AddArc(r.Left, r.Top, dx, dy, 180, 90);
        path.AddArc(r.Right - dx, r.Top, dx, dy, 270, 90);
        path.AddArc(r.Right - dx, r.Bottom - dy, dx, dy, 0, 90);
        path.AddArc(r.Left, r.Bottom - dy, dx, dy, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath Circle(XElement element)
    {
        double cx = Value(element, "cx") ?? 0;
        double cy = Value(element, "cy") ?? 0;
        double r = Value(element, "r") ?? throw new SvgNotSupportedException("circle に r が無い");

        var path = new GraphicsPath();
        path.AddEllipse((float)(cx - r), (float)(cy - r), (float)(r * 2), (float)(r * 2));
        return path;
    }

    private static GraphicsPath Ellipse(XElement element)
    {
        double cx = Value(element, "cx") ?? 0;
        double cy = Value(element, "cy") ?? 0;
        double rx = Value(element, "rx") ?? throw new SvgNotSupportedException("ellipse に rx が無い");
        double ry = Value(element, "ry") ?? throw new SvgNotSupportedException("ellipse に ry が無い");

        var path = new GraphicsPath();
        path.AddEllipse((float)(cx - rx), (float)(cy - ry), (float)(rx * 2), (float)(ry * 2));
        return path;
    }

    private static GraphicsPath PathData(XElement element)
    {
        string data = (string?)element.Attribute("d")
            ?? throw new SvgNotSupportedException("path に d が無い");
        return PathParser.Parse(data, (string?)element.Attribute("id"));
    }

    // ---- 属性の読み取り ------------------------------------------------------

    /// <summary>塗りの指定。属性と style のどちらに書かれていても拾う。</summary>
    private readonly record struct Style(Color Fill, string? Stroke, bool Hidden, bool EvenOdd)
    {
        public static Style Of(XElement element)
        {
            Dictionary<string, string> declarations = ParseStyle((string?)element.Attribute("style"));

            string? fill = Lookup(element, declarations, "fill");
            string? fillOpacity = Lookup(element, declarations, "fill-opacity");
            string? fillRule = Lookup(element, declarations, "fill-rule");
            string? stroke = Lookup(element, declarations, "stroke");
            string? display = Lookup(element, declarations, "display");

            if (stroke is not null && stroke.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                stroke = null;
            }

            return new Style(
                Fill: ParseColor(fill, fillOpacity),
                Stroke: stroke,
                Hidden: display is not null && display.Equals("none", StringComparison.OrdinalIgnoreCase),
                EvenOdd: fillRule is not null && fillRule.Equals("evenodd", StringComparison.OrdinalIgnoreCase));
        }

        private static string? Lookup(XElement element, Dictionary<string, string> declarations, string key) =>
            declarations.TryGetValue(key, out string? fromStyle) ? fromStyle : (string?)element.Attribute(key);

        private static Dictionary<string, string> ParseStyle(string? style)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (style is null) return result;

            foreach (string declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = declaration.IndexOf(':');
                if (colon <= 0) continue;
                result[declaration[..colon].Trim()] = declaration[(colon + 1)..].Trim();
            }
            return result;
        }

        private static Color ParseColor(string? fill, string? opacity)
        {
            // SVG の既定の塗りは黒
            if (fill is null) return Color.Black;
            if (fill.Equals("none", StringComparison.OrdinalIgnoreCase)) return Color.Transparent;

            if (!fill.StartsWith('#'))
            {
                throw new SvgNotSupportedException(
                    $"塗りの指定 '{fill}' は解釈できない。#rrggbb か #rgb で書くこと（色名・gradient は非対応）");
            }

            string hex = fill[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            }
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                throw new SvgNotSupportedException($"塗りの指定 '{fill}' を色として読めない");
            }

            int alpha = 255;
            if (opacity is not null &&
                double.TryParse(opacity, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                alpha = (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
            }

            return Color.FromArgb(alpha, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
    }

    private static double? Value(XElement element, string name)
    {
        string? raw = (string?)element.Attribute(name);
        if (raw is null) return null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new SvgNotSupportedException($"{element.Name.LocalName} の {name}='{raw}' を数として読めない");
        }
        return value;
    }

    internal static double[] Numbers(string text) =>
        text.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToArray();
}
