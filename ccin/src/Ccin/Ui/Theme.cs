using Ccin.Core;

namespace Ccin.Ui;

/// <summary>
/// 画面の配色を 1 箇所に閉じ込める。
///
/// .NET 10 の <see cref="Application.SetColorMode"/> が枠・スクロールバー・一覧の下地・
/// 右クリックメニューまで面倒を見る（実測）。ここが受け持つのは、その上に載せる
/// 「らしさ」の側だけ。実測で分かった**フレームワークが面倒を見ない 2 箇所**は
/// <see cref="Grid"/> と <see cref="DrawComboItem"/> で明示的に埋める。
///
/// 色を <see cref="Color"/> の定数ではなくプロパティで返しているのは、設定で
/// ライトへ戻せる逃げ道（<see cref="AppSettings.DarkMode"/>）を残しているため。
/// 既定はダーク。切り替えの UI は置かない（要求はダークモードであり、
/// 下段にボタンを 1 個増やすと送信先の欄がその分だけ狭くなる）。
/// </summary>
internal static class Theme
{
    /// <summary>ダークで描くか。既定は true。設定ファイルの隠し項目でだけ変えられる。</summary>
    public static bool Dark => AppSettings.DarkMode;

    // Anthropic 風の配色。地が中性の灰ではなく僅かに暖色へ寄っているのが特徴で、
    // 差し色は同社のコーラル（coral / clay）に合わせてある。
    private static readonly Color DarkBase = Color.FromArgb(0x26, 0x26, 0x24);
    private static readonly Color DarkField = Color.FromArgb(0x1F, 0x1E, 0x1D);
    private static readonly Color DarkSurface = Color.FromArgb(0x30, 0x30, 0x2E);
    private static readonly Color DarkText = Color.FromArgb(0xF0, 0xEE, 0xE6);
    private static readonly Color DarkMuted = Color.FromArgb(0x8A, 0x85, 0x7B);
    private static readonly Color DarkLine = Color.FromArgb(0x3E, 0x3E, 0x3B);
    private static readonly Color DarkSelect = Color.FromArgb(0x3A, 0x3A, 0x37);
    private static readonly Color CoralBase = Color.FromArgb(0xD9, 0x77, 0x57);
    private static readonly Color CoralHover = Color.FromArgb(0xE0, 0x8A, 0x6D);
    private static readonly Color CoralPress = Color.FromArgb(0xC2, 0x5F, 0x3F);

    /// <summary>窓の地。</summary>
    public static Color Base => Dark ? DarkBase : SystemColors.Control;

    /// <summary>文字を打つ場所・表の地。窓の地より一段落とす。</summary>
    public static Color Field => Dark ? DarkField : SystemColors.Window;

    /// <summary>ボタン・見出しなど、地から起こす面。</summary>
    public static Color Surface => Dark ? DarkSurface : SystemColors.Control;

    /// <summary>主たる文字。</summary>
    public static Color Text => Dark ? DarkText : SystemColors.ControlText;

    /// <summary>添え物の文字（ヒント行）。</summary>
    public static Color Muted => Dark ? DarkMuted : SystemColors.GrayText;

    /// <summary>罫線・枠。</summary>
    public static Color Line => Dark ? DarkLine : SystemColors.ControlDark;

    /// <summary>選択されている行。</summary>
    public static Color Select => Dark ? DarkSelect : SystemColors.Highlight;

    /// <summary>差し色。送信ボタンと一覧の選択行にだけ使う。散らすと差し色でなくなる。</summary>
    public static Color Accent => Dark ? CoralBase : SystemColors.Control;

    /// <summary>差し色の上に載せる文字。</summary>
    public static Color OnAccent => Dark ? Color.White : SystemColors.ControlText;

    /// <summary>窓そのもの。タイトルバーは <see cref="Application.SetColorMode"/> が持っていく。</summary>
    public static void Window(Form form)
    {
        form.BackColor = Base;
        form.ForeColor = Text;
    }

    /// <summary>本文の入力欄・パス入力欄。</summary>
    public static void Input(TextBox box)
    {
        box.BackColor = Field;
        box.ForeColor = Text;
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    /// <summary>下段のような、地の上に置くだけの入れ物。</summary>
    public static void Surface_(Control control) => control.BackColor = Base;

    /// <summary>ふつうのボタン。</summary>
    public static void Button(Button button) => Paint(button, Surface, Text, primary: false);

    /// <summary>
    /// 主たる操作（送信 / OK）。ここだけ差し色で立てる。
    ///
    /// 差し色のボタンは、無効にしても地の色が残る。WinForms が薄くするのは文字だけで、
    /// 背景は指定した色のまま描かれるため、**押せないのに押せるように見える**。
    /// 送信ボタンは Claude Code が 1 つも無いときに無効になる要件があり、
    /// そこで見分けが付かないのは危ない。有効・無効で地の色ごと入れ替える。
    /// </summary>
    public static void Primary(Button button)
    {
        Paint(button, Accent, OnAccent, primary: true);
        if (!Dark) return;

        button.EnabledChanged -= OnPrimaryEnabledChanged;
        button.EnabledChanged += OnPrimaryEnabledChanged;
        ApplyPrimaryState(button);
    }

    private static void OnPrimaryEnabledChanged(object? sender, EventArgs e)
    {
        if (sender is Button button) ApplyPrimaryState(button);
    }

    private static void ApplyPrimaryState(Button button)
    {
        button.BackColor = button.Enabled ? Accent : Surface;
        button.ForeColor = button.Enabled ? OnAccent : Muted;
        button.FlatAppearance.BorderColor = button.Enabled ? CoralPress : Line;
    }

    private static void Paint(Button button, Color back, Color fore, bool primary)
    {
        if (!Dark)
        {
            button.FlatStyle = FlatStyle.Standard;
            button.UseVisualStyleBackColor = true;
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = back;
        button.ForeColor = fore;
        button.FlatAppearance.BorderColor = primary ? CoralPress : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? CoralHover : Select;
        button.FlatAppearance.MouseDownBackColor = primary ? CoralPress : Line;
    }

    /// <summary>チェックボックス。地の色は置かれている場所に合わせて渡す。</summary>
    public static void Check(CheckBox check, Color background)
    {
        check.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
        check.BackColor = background;
        check.ForeColor = Text;
    }

    /// <summary>文字だけの部品。<paramref name="muted"/> は添え物の扱い。</summary>
    public static void Text_(Label label, Color background, bool muted = false)
    {
        label.BackColor = background;
        label.ForeColor = muted ? Muted : Text;
    }

    /// <summary>
    /// 送信先の一覧。
    ///
    /// 閉じた状態も開いた一覧も下地はフレームワークが暗くするが、**選択行だけは
    /// OS の青のまま残る**（実測）。配色から浮くので自前で描く。
    /// </summary>
    public static void Combo(ComboBox combo)
    {
        combo.FlatStyle = Dark ? FlatStyle.Flat : FlatStyle.Standard;
        combo.BackColor = Dark ? Surface : SystemColors.Window;
        combo.ForeColor = Text;
        combo.DrawMode = Dark ? DrawMode.OwnerDrawFixed : DrawMode.Normal;
    }

    /// <summary><see cref="ComboBox.DrawItem"/> から呼ぶ。<see cref="Combo"/> と対で使う。</summary>
    public static void DrawComboItem(ComboBox combo, DrawItemEventArgs e)
    {
        if (!Dark || e.Index < 0 || e.Index >= combo.Items.Count) return;

        // 閉じている状態（ComboBoxEdit）は「選択されている」扱いで飛んでくるが、
        // ここで差し色を塗ると下段が常時オレンジになる。開いた一覧のときだけ塗る
        bool closed = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool selected = !closed && (e.State & DrawItemState.Selected) != 0;

        Color back = closed ? Surface : selected ? Accent : Field;
        Color fore = selected ? OnAccent : Text;

        using var brush = new SolidBrush(back);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics,
            combo.Items[e.Index]?.ToString(),
            e.Font ?? combo.Font,
            e.Bounds,
            fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// 送信先の表。
    ///
    /// <see cref="Application.SetColorMode"/> が唯一面倒を見なかった部品（実測）。
    /// 見出しは <see cref="DataGridView.EnableHeadersVisualStyles"/> を false にしないと、
    /// 色を指定しても Windows の視覚スタイルが上書きして白のまま残る。
    /// </summary>
    public static void Grid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = !Dark;
        grid.BackgroundColor = Field;
        grid.GridColor = Line;

        grid.DefaultCellStyle.BackColor = Field;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Select;
        grid.DefaultCellStyle.SelectionForeColor = Text;

        grid.ColumnHeadersDefaultCellStyle.BackColor = Surface;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
    }

    /// <summary>
    /// 表のチェックボックスのセル。
    ///
    /// OS が描くチェックは入っているとき青（Windows のアクセント色）になり、配色から浮く。
    /// 送信ボタンと同じ差し色に揃えるため、箱とチェックだけ自前で描く。
    /// 地と罫線は表の描画に任せる（列幅や選択の見え方を自分で持たないため）。
    ///
    /// 描画を置き換えるだけで当たり判定は触っていないので、クリックの挙動は変わらない。
    /// </summary>
    public static void DrawCheckCell(DataGridViewCellPaintingEventArgs e, bool isChecked)
    {
        if (e.Graphics is not Graphics graphics) return;

        // 地・罫線・選択の見え方は表に描かせ、チェックの絵柄だけ自分で差し替える
        e.Paint(e.ClipBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);

        // 箱の大きさはセルから割り出す。DPI ごとの数値を自分で持たないため
        int side = CheckSide(Math.Min(e.CellBounds.Width, e.CellBounds.Height));
        var box = new Rectangle(
            e.CellBounds.X + ((e.CellBounds.Width - side) / 2),
            e.CellBounds.Y + ((e.CellBounds.Height - side) / 2),
            side,
            side);

        DrawCheckGlyph(graphics, box, isChecked, enabled: true);
    }

    /// <summary>
    /// チェックの絵柄の一辺。入れ物の短い方から決める。
    /// 表のセルと下段のチェックボックスで同じ比率を使い、絵柄を揃えるためにここに集約する。
    /// </summary>
    public static int CheckSide(int available) => Math.Max(10, (int)(available * 0.55));

    /// <summary>
    /// チェックの絵柄そのもの。表のセルと下段のチェックボックスが共有する。
    /// 片方だけ描き替えると見た目がずれるので、描く場所を増やすときも必ずここを通すこと。
    /// </summary>
    public static void DrawCheckGlyph(Graphics graphics, Rectangle box, bool isChecked, bool enabled)
    {
        int side = Math.Min(box.Width, box.Height);
        Color fillColor = enabled ? Accent : Surface;
        Color markColor = enabled ? OnAccent : Muted;
        Color edgeColor = enabled ? Muted : Line;

        var mode = graphics.SmoothingMode;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        try
        {
            if (isChecked)
            {
                using var fill = new SolidBrush(fillColor);
                graphics.FillRectangle(fill, box);

                // チェックの線。太さは箱に比例させる
                using var pen = new Pen(markColor, Math.Max(1.6f, side * 0.14f))
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                };
                graphics.DrawLines(pen,
                [
                    new PointF(box.Left + (side * 0.24f), box.Top + (side * 0.52f)),
                    new PointF(box.Left + (side * 0.43f), box.Top + (side * 0.71f)),
                    new PointF(box.Left + (side * 0.76f), box.Top + (side * 0.30f)),
                ]);
            }
            else
            {
                // 枠に罫線と同じ色を使ってはいけない。選択行の地（Select）とほぼ同じ明るさで、
                // 選択されている行のチェックが外れた箱が見えなくなる（実機で確認）。
                // 地が Field でも Select でも差が出る明るさを使う
                using var pen = new Pen(edgeColor, Math.Max(1f, side * 0.10f));
                graphics.DrawRectangle(pen, box);
            }
        }
        finally
        {
            graphics.SmoothingMode = mode;
        }
    }

    /// <summary>
    /// 表がセルの編集用に作る部品。表本体の指定は受け継がないので、作られた瞬間に当てる。
    /// 文字選択の色（青）は OS が描くもので、ここでは変えられない。Windows 標準のアプリと
    /// 同じ見え方になるので、そのままにしてある。
    /// </summary>
    public static void Editor(Control editor)
    {
        editor.BackColor = Field;
        editor.ForeColor = Text;
    }
}
