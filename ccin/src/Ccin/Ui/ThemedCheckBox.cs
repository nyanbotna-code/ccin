namespace Ccin.Ui;

/// <summary>
/// 配色に合わせて自分で描くチェックボックス。
///
/// OS が描くチェックは入っているとき Windows のアクセント色（青）になり、送信ボタンや
/// Edit ダイアログの「表示」列と色が食い違う。絵柄は <see cref="Theme.DrawCheckGlyph"/> を
/// 共有しているので、表のセルと同じものが出る。
///
/// 描くのは見た目だけで、押したときの動き・キー操作・Tab の順番は
/// <see cref="CheckBox"/> のまま。ライト側（<see cref="Theme.Dark"/> が false）では
/// 自前描画をやめて OS に任せる。
/// </summary>
internal sealed class ThemedCheckBox : CheckBox
{
    /// <summary>箱と文字の間。論理値。</summary>
    private const int GapLogical = 6;

    private bool _hovered;

    public ThemedCheckBox()
    {
        if (!Theme.Dark) return;

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor,
            true);
    }

    // 見た目が変わる出来事のたびに描き直す。UserPaint では自動では来ない
    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!Theme.Dark)
        {
            base.OnPaint(e);
            return;
        }

        Graphics graphics = e.Graphics;
        using (var background = new SolidBrush(BackColor))
        {
            graphics.FillRectangle(background, ClientRectangle);
        }

        // 箱の大きさは表のセルと同じ決め方。並べたときに絵柄が揃う
        int side = Theme.CheckSide(ClientSize.Height);
        var box = new Rectangle(0, (ClientSize.Height - side) / 2, side, side);

        Theme.DrawCheckGlyph(graphics, box, Checked, Enabled);

        // 触れている間はうっすら囲って、押せることを見せる
        if (_hovered && Enabled)
        {
            using var pen = new Pen(Theme.Accent, 1f);
            var halo = Rectangle.Inflate(box, LogicalToDeviceUnits(2), LogicalToDeviceUnits(2));
            graphics.DrawRectangle(pen, halo);
        }

        int textLeft = box.Right + LogicalToDeviceUnits(GapLogical);
        var textArea = new Rectangle(
            textLeft, 0, Math.Max(0, ClientSize.Width - textLeft), ClientSize.Height);

        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            textArea,
            Enabled ? Theme.Text : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // Tab で回ってきたことが分かるように。枠が無いと今どこにいるか読めない
        if (Focused)
        {
            ControlPaint.DrawFocusRectangle(graphics, textArea);
        }
    }
}
