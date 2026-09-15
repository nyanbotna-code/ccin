using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;

namespace MakeIcon;

/// <summary>
/// SVG の <c>d</c> 属性を読む。アイコンで使う分だけ。
///
/// 対応: m M l L h H v V c C s S z Z
/// 非対応: q t a（出会ったら例外。黙って直線に落とすと絵が変わるため）
///
/// 数の切れ目は空白・カンマのほか、**符号や 2 つ目の小数点でも切れる**
/// （`1.5-2.3` や `.5.5` のような書き方を Inkscape が出す）。
/// </summary>
internal static class PathParser
{
    public static GraphicsPath Parse(string data, string? id)
    {
        var path = new GraphicsPath();
        var tokens = new Tokenizer(data);

        PointF current = PointF.Empty;
        PointF start = PointF.Empty;
        PointF lastControl = PointF.Empty;   // s / S が折り返すのに使う
        bool lastWasCubic = false;
        char command = '\0';
        bool figureOpen = false;

        while (true)
        {
            char? next = tokens.PeekCommand();
            if (next is char letter)
            {
                tokens.TakeCommand();
                command = letter;
            }
            else if (tokens.AtEnd)
            {
                break;
            }
            else if (command == '\0')
            {
                throw new SvgNotSupportedException($"path (id={id}) が命令以外で始まっている");
            }
            else if (command is 'm') command = 'l';      // m の 2 個目以降は l 扱い（SVG の決まり）
            else if (command is 'M') command = 'L';

            bool relative = char.IsLower(command);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    if (figureOpen) path.CloseFigure();
                    current = Point(tokens, current, relative);
                    start = current;
                    figureOpen = false;
                    lastWasCubic = false;
                    break;

                case 'L':
                {
                    PointF to = Point(tokens, current, relative);
                    path.AddLine(current, to);
                    current = to;
                    figureOpen = true;
                    lastWasCubic = false;
                    break;
                }

                case 'H':
                {
                    float x = (float)tokens.Number();
                    var to = new PointF(relative ? current.X + x : x, current.Y);
                    path.AddLine(current, to);
                    current = to;
                    figureOpen = true;
                    lastWasCubic = false;
                    break;
                }

                case 'V':
                {
                    float y = (float)tokens.Number();
                    var to = new PointF(current.X, relative ? current.Y + y : y);
                    path.AddLine(current, to);
                    current = to;
                    figureOpen = true;
                    lastWasCubic = false;
                    break;
                }

                case 'C':
                {
                    PointF c1 = Point(tokens, current, relative);
                    PointF c2 = Point(tokens, current, relative);
                    PointF to = Point(tokens, current, relative);
                    path.AddBezier(current, c1, c2, to);
                    lastControl = c2;
                    current = to;
                    figureOpen = true;
                    lastWasCubic = true;
                    break;
                }

                case 'S':
                {
                    // 1 つ目の制御点は、直前の 2 つ目の制御点を現在地で折り返したもの
                    PointF c1 = lastWasCubic
                        ? new PointF((current.X * 2) - lastControl.X, (current.Y * 2) - lastControl.Y)
                        : current;
                    PointF c2 = Point(tokens, current, relative);
                    PointF to = Point(tokens, current, relative);
                    path.AddBezier(current, c1, c2, to);
                    lastControl = c2;
                    current = to;
                    figureOpen = true;
                    lastWasCubic = true;
                    break;
                }

                case 'Z':
                    path.CloseFigure();
                    current = start;
                    figureOpen = false;
                    lastWasCubic = false;
                    break;

                default:
                    throw new SvgNotSupportedException(
                        $"path (id={id}) の命令 '{command}' は非対応。" +
                        "対応しているのは m M l L h H v V c C s S z Z。" +
                        "Inkscape の [パス] → [オブジェクトをパスへ] で書き出し直すか、png 経由で作ること");
            }

            if (tokens.AtEnd) break;
        }

        return path;
    }

    private static PointF Point(Tokenizer tokens, PointF origin, bool relative)
    {
        float x = (float)tokens.Number();
        float y = (float)tokens.Number();
        return relative ? new PointF(origin.X + x, origin.Y + y) : new PointF(x, y);
    }

    private sealed class Tokenizer(string text)
    {
        private int _index;

        public bool AtEnd
        {
            get
            {
                SkipSeparators();
                return _index >= text.Length;
            }
        }

        public char? PeekCommand()
        {
            SkipSeparators();
            if (_index >= text.Length) return null;
            char c = text[_index];
            return char.IsLetter(c) ? c : null;
        }

        public void TakeCommand() => _index++;

        public double Number()
        {
            SkipSeparators();
            if (_index >= text.Length) throw new SvgNotSupportedException("path の途中で数が足りない");

            var builder = new StringBuilder();
            bool seenDot = false;

            if (text[_index] is '-' or '+') builder.Append(text[_index++]);

            while (_index < text.Length)
            {
                char c = text[_index];
                if (char.IsAsciiDigit(c))
                {
                    builder.Append(c);
                    _index++;
                }
                else if (c == '.' && !seenDot)
                {
                    seenDot = true;
                    builder.Append(c);
                    _index++;
                }
                else if ((c is 'e' or 'E') && builder.Length > 0)
                {
                    builder.Append(c);
                    _index++;
                    if (_index < text.Length && text[_index] is '-' or '+') builder.Append(text[_index++]);
                }
                else
                {
                    break;   // 2 つ目の小数点・次の符号・区切り。ここで数が終わる
                }
            }

            if (!double.TryParse(builder.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw new SvgNotSupportedException($"path の '{builder}' を数として読めない");
            }
            return value;
        }

        private void SkipSeparators()
        {
            while (_index < text.Length && (char.IsWhiteSpace(text[_index]) || text[_index] == ','))
            {
                _index++;
            }
        }
    }
}
