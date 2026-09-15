using System.Diagnostics;
using System.IO;
using Ccin.Core;

namespace Ccin.Ui;

/// <summary>
/// 送信先の表示名・番号・除外を編集する画面と、画像のワークフォルダ設定。
///
/// 送信先が Claude Code だけでなく端末タブ全てになったため、一覧には同じ名前
/// （`powershell` 等）がいくつも並ぶ。名前を付けられること・要らないものを畳めることが、
/// 拡大後は「あると便利」ではなく「無いと使えない」側に寄った。
///
/// 変更は OK を押すまで反映しない。編集の途中の状態が送信先へ効いてしまうと、
/// 並べ替えの最中に送信できる状態が生まれる。
///
/// ワークフォルダを同じ画面に置いているのは、設定用の窓を 2 つ作らないため。
/// 送信先とは無関係なので、下端に分けて置く。
/// </summary>
internal sealed class EditDialog : Form
{
    private const int NumberColumnLogicalWidth = 44;
    private const int NameColumnLogicalWidth = 150;
    private const int RealNameColumnLogicalWidth = 210;

    /// <summary>「表示」の 2 文字と余白が入る幅。48 では見出しが「表」で切れた。</summary>
    private const int ShowColumnLogicalWidth = 60;

    /// <summary>ログのファイル名。`20260913_205300.log` が省略されずに収まる幅。</summary>
    private const int LogColumnLogicalWidth = 170;

    /// <summary>ログを開く「…」ボタン。押せる最小限に留め、表の横幅を圧迫しない。</summary>
    private const int LogOpenColumnLogicalWidth = 34;

    /// <summary>行の高さ（論理値）。文字の高さに余白を足した値。</summary>
    private const int RowLogicalHeight = 26;

    /// <summary>見出しの高さ（論理値）。行より少し高くして、上下で切れないようにする。</summary>
    private const int HeaderLogicalHeight = 30;

    /// <summary>既定の大きさ（論理値）。4 列と余白が収まる幅にしてある。</summary>
    private const int DefaultLogicalWidth = 760;
    private const int DefaultLogicalHeight = 380;

    /// <summary>これ以上は縮められない大きさ（論理値）。</summary>
    /// <summary>
    /// ログの列（170）と「…」（34）を足したぶん広げてある。広げないと、既定の大きさで
    /// 開いたときに新しい 2 列が右へ見切れる。
    /// </summary>
    /// <summary>
    /// 狭める限界。2 つの要求のうち大きいほうで決まる。
    ///
    /// - 表の列: 44 + 150 + 210 + 60 + 170 + 34 = 668
    /// - 見え方の行: 半透明 170 + スライダー 160 + 値 52 + 最前面 110 + 画像 150 と間隔 = 698
    ///
    /// 内側で 698 要るので、枠の分を足して 724 にしてある。
    /// </summary>
    private const int MinimumLogicalWidth = 724;
    /// <summary>
    /// ログの保存先と、窓の見え方の行を足したぶん 2 行（34 = 行の高さ 28 + 間隔 6）
    /// 増やしてある。増やさないと、縮めたときにグリッドが潰れる前に下の行がはみ出す。
    /// </summary>
    private const int MinimumLogicalHeight = 388;

    /// <summary>覚えた位置を使ってよいと判断する、画面に掛かる最小の大きさ（物理ピクセル）。</summary>
    private const int MinimumVisibleWidth = 160;
    private const int MinimumVisibleHeight = 40;

    private readonly DataGridView _grid = new();
    private readonly Button _up = new();
    private readonly Button _down = new();
    private readonly Button _compact = new();
    private readonly Button _clearName = new();
    private readonly Label _folderLabel = new();
    private readonly TextBox _folder = new();
    private readonly Button _browse = new();
    private readonly Button _open = new();
    private readonly Label _logLabel = new();
    private readonly TextBox _logFolder = new();
    private readonly Button _logBrowse = new();
    private readonly Button _logOpen = new();
    private readonly ThemedCheckBox _fade = new();
    private readonly TrackBar _opacity = new();
    private readonly Label _opacityValue = new();
    private readonly ThemedCheckBox _onTop = new();
    private readonly ThemedCheckBox _previewImage = new();
    private readonly Button _ok = new();
    private readonly Button _cancel = new();

    private readonly List<Row> _rows = [];

    /// <summary>編集中の 1 行。グリッドの並び順が番号順になる。</summary>
    private sealed class Row
    {
        public required TerminalTab Tab { get; init; }
        public required int Number { get; set; }
        public string? DisplayName { get; set; }
        public bool Visible { get; set; }

        /// <summary>
        /// 送信ログのファイル名。<see cref="MoveRow"/> は行ごと入れ替えるので、
        /// 上下に動かしてもこの値は行に付いて動く（番号だけが相手と入れ替わる）。
        /// </summary>
        public required string LogName { get; set; }
    }

    /// <summary>OK で閉じたときの編集結果。並び順が番号順。</summary>
    public List<TargetEdit> Result { get; private set; } = [];

    /// <summary>OK で閉じたときのワークフォルダ。</summary>
    public string ImageFolder { get; private set; } = AppSettings.ImageFolder;

    /// <summary>OK で閉じたときの送信ログの保存先。</summary>
    public string LogFolder { get; private set; } = AppSettings.LogFolder;

    /// <summary>OK で閉じたときの「非アクティブ時に半透明」。</summary>
    public bool FadeWhenInactive { get; private set; } = AppSettings.FadeWhenInactive;

    /// <summary>OK で閉じたときの非アクティブ時の不透明度（パーセント）。</summary>
    public int InactiveOpacity { get; private set; } = AppSettings.InactiveOpacity;

    /// <summary>OK で閉じたときの「常に最前面」。</summary>
    public bool AlwaysOnTop { get; private set; } = AppSettings.AlwaysOnTop;

    /// <summary>OK で閉じたときの「貼った画像を開く」。</summary>
    public bool PreviewImage { get; private set; } = AppSettings.PreviewImage;

    /// <summary>
    /// 透過率を動かしている最中に、保存前の値で見え方を伝える。
    ///
    /// 下見が無いと、数字だけ見て決めることになる。このダイアログが前面にいる間、
    /// 親の窓はちょうど非アクティブなので、設定した見え方がそのまま確かめられる。
    /// </summary>
    public event Action<bool, int>? TransparencyPreview;

    public EditDialog(IReadOnlyList<NumberedTab> targets)
    {
        foreach (NumberedTab target in targets)
        {
            _rows.Add(new Row
            {
                Tab = target.Tab,
                Number = target.Number,
                DisplayName = target.DisplayName,
                Visible = !target.Excluded,
                LogName = target.LogName,
            });
        }

        BuildUi();
        WireEvents();
        Reload(0);
    }

    private void BuildUi()
    {
        Text = "送信先の編集";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        // 仮の大きさ。ここではハンドルがまだ無く、DPI が 96 として計算されるため、
        // 実際の大きさと位置は表示後に <see cref="ApplyInitialBounds"/> で決め直す
        ClientSize = LogicalToDeviceUnits(new Size(DefaultLogicalWidth, DefaultLogicalHeight));

        // 親（メイン画面）が最前面なので、こちらも上げないと後ろへ回り込む
        TopMost = true;

        _grid.Name = "targetGrid";
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;

        // フォントは指定しない。フォームから継承させ、メイン画面と同じ大きさに揃える。
        // 以前ここで 10pt を指定していたが、独自の大きさを持ち込む理由が無かった。

        // 高さと配置は既定に任せない。既定のままだと見出しの文字が上下で切れ、
        // 行を高くしたときに文字が天井へ張り付く（いずれも実機で確認）
        _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4));
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;

        var number = new DataGridViewTextBoxColumn
        {
            Name = "no",
            HeaderText = "No",
            ReadOnly = true,
            Width = LogicalToDeviceUnits(NumberColumnLogicalWidth),
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        var display = new DataGridViewTextBoxColumn
        {
            Name = "displayName",
            HeaderText = "表示名",
            Width = LogicalToDeviceUnits(NameColumnLogicalWidth),
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        var real = new DataGridViewTextBoxColumn
        {
            Name = "realName",
            HeaderText = "実タブ名",
            ReadOnly = true,
            Width = LogicalToDeviceUnits(RealNameColumnLogicalWidth),
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        var visible = new DataGridViewCheckBoxColumn
        {
            Name = "visible",
            HeaderText = "表示",
            Width = LogicalToDeviceUnits(ShowColumnLogicalWidth),
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        };

        var logName = new DataGridViewTextBoxColumn
        {
            Name = "logName",
            HeaderText = "ログ",
            Width = LogicalToDeviceUnits(LogColumnLogicalWidth),
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

        // 見出しは空。行ごとに「そのログを開く」だけの列で、並べ替えの対象にもしない
        var logOpen = new DataGridViewButtonColumn
        {
            Name = "logOpen",
            HeaderText = string.Empty,
            Text = "…",
            UseColumnTextForButtonValue = true,
            Width = LogicalToDeviceUnits(LogOpenColumnLogicalWidth),
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter },
        };

        _grid.Columns.AddRange([number, display, real, visible, logName, logOpen]);

        _up.Text = "▲";
        _down.Text = "▼";
        _compact.Text = "番号を詰める";
        _clearName.Text = "表示名を消す";
        _folderLabel.Text = "画像の保存先";
        _folderLabel.TextAlign = ContentAlignment.MiddleLeft;
        _browse.Text = "参照...";
        _open.Text = "開く";
        _logLabel.Text = "ログの保存先";
        _logLabel.TextAlign = ContentAlignment.MiddleLeft;
        _logBrowse.Text = "参照...";
        _logOpen.Text = "開く";
        _ok.Text = "OK";
        _cancel.Text = "キャンセル";

        _folder.Name = "imageFolderText";
        _folder.Text = AppSettings.ImageFolder;
        _logFolder.Name = "logFolderText";
        _logFolder.Text = AppSettings.LogFolder;

        _fade.Name = "fadeCheck";
        _fade.Text = "非アクティブ時に半透明";
        _fade.TextAlign = ContentAlignment.MiddleLeft;
        _fade.Checked = AppSettings.FadeWhenInactive;

        _onTop.Name = "onTopCheck";
        _onTop.Text = "常に最前面";
        _onTop.TextAlign = ContentAlignment.MiddleLeft;
        _onTop.Checked = AppSettings.AlwaysOnTop;

        _previewImage.Name = "previewImageCheck";
        _previewImage.Text = "貼った画像を開く";
        _previewImage.TextAlign = ContentAlignment.MiddleLeft;
        _previewImage.Checked = AppSettings.PreviewImage;

        // 目盛りは出さない。値はすぐ右の数字で読ませる
        _opacity.Name = "opacityBar";
        _opacity.Minimum = AppSettings.MinimumOpacity;
        _opacity.Maximum = 100;
        _opacity.TickStyle = TickStyle.None;
        _opacity.SmallChange = 5;
        _opacity.LargeChange = 10;
        _opacity.Value = AppSettings.InactiveOpacity;
        _opacity.Enabled = _fade.Checked;

        _opacityValue.Name = "opacityValueText";
        _opacityValue.TextAlign = ContentAlignment.MiddleLeft;
        _opacityValue.Text = OpacityText();

        _ok.DialogResult = DialogResult.OK;
        _cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = _ok;
        CancelButton = _cancel;

        Controls.AddRange(
            [_grid, _up, _down, _compact, _clearName,
             _fade, _opacity, _opacityValue, _onTop, _previewImage,
             _folderLabel, _folder, _browse, _open,
             _logLabel, _logFolder, _logBrowse, _logOpen,
             _ok, _cancel]);

        ApplyTheme();
    }

    /// <summary>
    /// 配色を当てる。位置と大きさには触らない（それは <see cref="LayoutControls"/> の担当）。
    ///
    /// 表はフレームワークのダーク対応が届かない唯一の部品なので、
    /// <see cref="Theme.Grid"/> で明示的に埋める。参照ボタンが開くフォルダ選択は
    /// OS の窓なので、こちらからは色を変えられない。
    /// </summary>
    private void ApplyTheme()
    {
        Theme.Window(this);
        Theme.Grid(_grid);

        Theme.Button(_up);
        Theme.Button(_down);
        Theme.Button(_compact);
        Theme.Button(_clearName);
        Theme.Button(_browse);
        Theme.Button(_open);
        Theme.Button(_logBrowse);
        Theme.Button(_logOpen);
        Theme.Button(_cancel);
        Theme.Primary(_ok);   // 主たる操作。メイン画面の送信ボタンと揃える

        Theme.Text_(_folderLabel, Theme.Base);
        Theme.Input(_folder);
        Theme.Text_(_logLabel, Theme.Base);
        Theme.Input(_logFolder);

        Theme.Check(_fade, Theme.Base);
        Theme.Check(_onTop, Theme.Base);
        Theme.Check(_previewImage, Theme.Base);
        Theme.Text_(_opacityValue, Theme.Base);

        // TrackBar は自前描画の対象外（つまみは OS が描く）。地だけでも合わせて、
        // 明るい帯が暗い画面の中に浮かないようにする
        _opacity.BackColor = Theme.Base;

        // セルの中のボタンは OS の既定色で描かれ、暗い表の中で白く浮く。
        // 表と同じ地に合わせる（チェックボックスを自前で描いているのと同じ理由）
        if (_grid.Columns["logOpen"] is DataGridViewButtonColumn logOpen)
        {
            logOpen.FlatStyle = FlatStyle.Flat;
            logOpen.DefaultCellStyle.BackColor = _grid.DefaultCellStyle.BackColor;
            logOpen.DefaultCellStyle.ForeColor = _grid.DefaultCellStyle.ForeColor;
            logOpen.DefaultCellStyle.SelectionBackColor = _grid.DefaultCellStyle.SelectionBackColor;
            logOpen.DefaultCellStyle.SelectionForeColor = _grid.DefaultCellStyle.SelectionForeColor;
        }
    }

    /// <summary>
    /// 部品の座標をその時点の DPI でまとめて決める。メイン画面と同じ方針で、
    /// 一部だけ古い倍率に取り残されないようにする。
    /// </summary>
    private void LayoutControls()
    {
        // 行と見出しの高さは DPI が変わるたびに取り直す。座標だけ新しい倍率で計算して
        // 高さが古いままだと、モニタを移った先で文字が切れる（メイン画面で踏んだのと同じ罠）
        ApplyGridMetrics();

        int pad = LogicalToDeviceUnits(10);
        int gap = LogicalToDeviceUnits(6);
        Size buttonSize = LogicalToDeviceUnits(new Size(104, 28));
        Size smallSize = LogicalToDeviceUnits(new Size(40, 28));
        int rowHeight = buttonSize.Height;

        int width = ClientSize.Width;
        int height = ClientSize.Height;

        int bottomRow = height - pad - rowHeight;
        int logRow = bottomRow - gap - rowHeight;
        int folderRow = logRow - gap - rowHeight;
        int optionRow = folderRow - gap - rowHeight;
        int toolRow = optionRow - gap - rowHeight;

        _grid.Location = new Point(pad, pad);
        _grid.Size = new Size(Math.Max(0, width - pad * 2), Math.Max(0, toolRow - gap - pad));

        _up.Size = smallSize;
        _down.Size = smallSize;
        _compact.Size = buttonSize;
        _clearName.Size = buttonSize;

        _up.Location = new Point(pad, toolRow);
        _down.Location = new Point(_up.Right + gap, toolRow);
        _compact.Location = new Point(_down.Right + gap * 2, toolRow);
        _clearName.Location = new Point(_compact.Right + gap, toolRow);

        // 窓の見え方の行。左から「半透明にするか」「どれくらい」「今の値」「常に最前面」。
        // スライダーは高さを持て余すので、行の中で少し下げて文字と目線を揃える
        _fade.Size = LogicalToDeviceUnits(new Size(170, 26));
        _opacity.Size = LogicalToDeviceUnits(new Size(160, 30));
        _opacityValue.Size = LogicalToDeviceUnits(new Size(52, 26));
        _onTop.Size = LogicalToDeviceUnits(new Size(110, 26));

        _previewImage.Size = LogicalToDeviceUnits(new Size(150, 26));

        _fade.Location = new Point(pad, optionRow);
        _opacity.Location = new Point(_fade.Right + gap, optionRow);
        _opacityValue.Location = new Point(_opacity.Right + gap, optionRow);
        _onTop.Location = new Point(_opacityValue.Right + gap * 2, optionRow);
        _previewImage.Location = new Point(_onTop.Right + gap * 2, optionRow);

        // 2 つのフォルダ行は同じ作りなので、同じ計算を 2 回使う。
        // 片方だけ座標をいじると行がずれるため、寸法は共通の変数に持つ
        Size labelSize = LogicalToDeviceUnits(new Size(84, 28));
        Size browseSize = LogicalToDeviceUnits(new Size(72, 28));
        Size openSize = LogicalToDeviceUnits(new Size(56, 28));
        int textOffset = LogicalToDeviceUnits(3);

        _folderLabel.Size = labelSize;
        _browse.Size = browseSize;
        _open.Size = openSize;
        _folderLabel.Location = new Point(pad, folderRow);
        _open.Location = new Point(Math.Max(0, width - pad - _open.Width), folderRow);
        _browse.Location = new Point(Math.Max(0, _open.Left - gap - _browse.Width), folderRow);
        _folder.Location = new Point(_folderLabel.Right + gap, folderRow + textOffset);
        _folder.Width = Math.Max(0, _browse.Left - gap - _folder.Left);

        _logLabel.Size = labelSize;
        _logBrowse.Size = browseSize;
        _logOpen.Size = openSize;
        _logLabel.Location = new Point(pad, logRow);
        _logOpen.Location = new Point(Math.Max(0, width - pad - _logOpen.Width), logRow);
        _logBrowse.Location = new Point(Math.Max(0, _logOpen.Left - gap - _logBrowse.Width), logRow);
        _logFolder.Location = new Point(_logLabel.Right + gap, logRow + textOffset);
        _logFolder.Width = Math.Max(0, _logBrowse.Left - gap - _logFolder.Left);

        _ok.Size = buttonSize;
        _cancel.Size = buttonSize;
        _cancel.Location = new Point(Math.Max(0, width - pad - _cancel.Width), bottomRow);
        _ok.Location = new Point(Math.Max(0, _cancel.Left - gap - _ok.Width), bottomRow);
    }

    /// <summary>
    /// 最初の大きさと位置を決める。**表示された後に呼ぶこと。**
    ///
    /// 覚えていれば前回の大きさと位置、無ければ既定。
    /// 既定の位置は**CCIN のいるモニタの中央**。親の中央（<c>CenterParent</c>）に出すと、
    /// メイン画面が画面の下寄りにあるときにダイアログの下側が画面外へはみ出す（実機で確認）。
    ///
    /// 覚えた位置は、モニタを外すと画面の外を指しうる。復元する前に画面内に入るかを確かめ、
    /// 入らなければ既定へ倒す。掴めない窓を作らないための保険。
    /// </summary>
    private void ApplyInitialBounds()
    {
        // 最小サイズもここで入れる。コンストラクタで入れると 96 DPI 換算になり、
        // 150% のモニタでは論理値の 2/3 まで縮められてしまう
        MinimumSize = LogicalToDeviceUnits(new Size(MinimumLogicalWidth, MinimumLogicalHeight));

        // 出す先のモニタ。親（CCIN メイン画面）が居るモニタに合わせる
        Rectangle work = Screen.FromControl((Control?)Owner ?? this).WorkingArea;

        // --- 大きさ
        // 覚えていないときの既定は、**このモニタの DPI で**計算する。コンストラクタの時点では
        // ハンドルが無く DPI が 96 になるため、150% のモニタでは 2/3 の幅で開き、
        // 列が右へはみ出して「表示」列が見えなくなる（実機で確認）
        Size size = AppSettings.EditSize
            ?? SizeFromClient(LogicalToDeviceUnits(new Size(DefaultLogicalWidth, DefaultLogicalHeight)));

        size = new Size(
            Math.Clamp(size.Width, MinimumSize.Width, Math.Max(MinimumSize.Width, work.Width)),
            Math.Clamp(size.Height, MinimumSize.Height, Math.Max(MinimumSize.Height, work.Height)));

        Size = size;

        // --- 位置
        Location = RestorableLocation(size) ?? Center(work, size);
    }

    /// <summary>クライアント領域の大きさから、枠を含む窓の大きさを出す。</summary>
    private Size SizeFromClient(Size client)
    {
        Size frame = Size - ClientSize;   // 枠とタイトルバーの分
        return new Size(client.Width + frame.Width, client.Height + frame.Height);
    }

    private static Point Center(Rectangle work, Size size) =>
        new(work.X + ((work.Width - size.Width) / 2), work.Y + ((work.Height - size.Height) / 2));

    /// <summary>
    /// 覚えている位置を、そのまま使えるなら返す。使えないなら null。
    ///
    /// 「使える」は、その位置に置いたときタイトルバーがどれかの画面の作業領域に
    /// 十分掛かること。掛かっていれば掴んで動かせる。
    /// </summary>
    private static Point? RestorableLocation(Size size)
    {
        if (AppSettings.EditLocation is not Point saved) return null;

        var bounds = new Rectangle(saved, size);
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle overlap = Rectangle.Intersect(screen.WorkingArea, bounds);
            if (overlap.Width >= MinimumVisibleWidth && overlap.Height >= MinimumVisibleHeight)
            {
                return saved;
            }
        }
        return null;
    }

    /// <summary>
    /// 今の大きさと位置を覚える。閉じ方（OK / キャンセル）にかかわらず記録する。
    /// 「広げて眺めたが今回は何も変えなかった」ときに忘れられるのは不便なため。
    /// </summary>
    private void RememberBounds()
    {
        if (WindowState != FormWindowState.Normal) return;   // 最小化中の値は覚えない

        AppSettings.EditSize = Size;
        AppSettings.EditLocation = Location;
        AppSettings.Save();
    }

    /// <summary>
    /// 行・見出しの高さと列幅を、その時点の DPI で入れ直す。
    ///
    /// 見出しの高さは <c>ColumnHeadersHeight</c> を直に指定する。AutoSize に任せると
    /// 環境によって文字の上下が詰まるため、必要な高さを自分で持つ。
    /// </summary>
    private void ApplyGridMetrics()
    {
        _grid.ColumnHeadersHeight = LogicalToDeviceUnits(HeaderLogicalHeight);
        _grid.RowTemplate.Height = LogicalToDeviceUnits(RowLogicalHeight);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Height = LogicalToDeviceUnits(RowLogicalHeight);
        }

        SetColumnWidth("no", NumberColumnLogicalWidth);
        SetColumnWidth("displayName", NameColumnLogicalWidth);
        SetColumnWidth("realName", RealNameColumnLogicalWidth);
        SetColumnWidth("visible", ShowColumnLogicalWidth);
    }

    private void SetColumnWidth(string name, int logicalWidth)
    {
        DataGridViewColumn? column = _grid.Columns[name];
        if (column is not null) column.Width = LogicalToDeviceUnits(logicalWidth);
    }

    private void WireEvents()
    {
        Resize += (_, _) => LayoutControls();
        DpiChanged += (_, _) => LayoutControls();
        FormClosing += (_, _) => RememberBounds();

        // 編集に入ると DataGridView が中に TextBox を作る。そのフォントはセルの指定を
        // そのまま受け継がないので、作られた瞬間に揃える。
        //
        // 揃える先は **セルの描画に実際に使われている** DefaultCellStyle.Font。
        // <see cref="Control.Font"/> を使ってはいけない。ウィンドウが高 DPI のモニタへ出ると
        // WinForms が Control.Font だけを作り直すことがあり、セル側（DefaultCellStyle.Font）と
        // 食い違う。実機で「開いた直後に編集すると文字が大きく、確定すると元に戻る」症状が出た。
        // ここで毎回セル側から取り直せば、どちらが作り直されても食い違わない。
        _grid.EditingControlShowing += (_, e) =>
        {
            // 表が編集用に作る部品は、表本体の色指定を受け継がない。作られた瞬間に当てる
            Theme.Editor(e.Control);

            if (e.Control is TextBox box)
            {
                box.BorderStyle = BorderStyle.None;   // 枠を二重にしない
                box.Multiline = false;
            }

            // DataGridView が編集用コントロールを**新しく作った**とき、WinForms はその後で
            // DPI 倍率をもう一度掛ける。実測（150% のモニタ、DPI 144、フォントは継承）:
            //
            //   イベント内   セル 13.5pt  編集用 13.5pt
            //   その直後     セル 13.5pt  編集用 20.25pt   ← 13.5 × 1.5
            //
            // 独自フォントを指定していてもいなくても同じ挙動だった。イベントの中で入れても
            // 上書きされるので、メッセージを回した後に当て直す。コントロールは使い回されるため、
            // 作り直されるのは 1 回目だけ（＝「開いた直後の 1 回目だけ大きい」の正体）。
            Font? cellFont = e.CellStyle?.Font;
            if (cellFont is null) return;

            BeginInvoke(() =>
            {
                Control? editor = _grid.EditingControl;
                if (editor is not null && editor.Font.Size != cellFont.Size) editor.Font = cellFont;
            });
        };

        // 「表示」列のチェックだけ、送信ボタンと同じ差し色で描く。
        // OS に任せると入っているとき Windows のアクセント色（青）になり、配色から浮く
        _grid.CellPainting += (_, e) =>
        {
            if (!Theme.Dark || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_grid.Columns[e.ColumnIndex].Name != "visible") return;

            Theme.DrawCheckCell(e, e.Value is true);
            e.Handled = true;
        };

        _up.Click += (_, _) => MoveRow(-1);
        _down.Click += (_, _) => MoveRow(1);
        _compact.Click += (_, _) => Compact();
        _clearName.Click += (_, _) => ClearName();
        _browse.Click += (_, _) => Browse(_folder, "画像の保存先を選んで");
        _open.Click += (_, _) => OpenFolder(_folder);
        _logBrowse.Click += (_, _) => Browse(_logFolder, "ログの保存先を選んで");
        _logOpen.Click += (_, _) => OpenFolder(_logFolder);

        // チェックはセルを離れるまで確定しないので、その場で確定させる。
        // 確定しないまま OK を押すと、見えている状態と保存される状態が食い違う
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
            Row row = _rows[e.RowIndex];

            if (_grid.Columns[e.ColumnIndex].Name == "displayName")
            {
                string? value = _grid.Rows[e.RowIndex].Cells["displayName"].Value?.ToString();
                row.DisplayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            else if (_grid.Columns[e.ColumnIndex].Name == "visible")
            {
                row.Visible = _grid.Rows[e.RowIndex].Cells["visible"].Value is true;
            }
            else if (_grid.Columns[e.ColumnIndex].Name == "logName")
            {
                // ここでは打った値をそのまま預かる。空・重複・使えない文字の始末は
                // TargetNumbering.Apply が最後にまとめて行う（決める場所を 1 つにする）
                string? value = _grid.Rows[e.RowIndex].Cells["logName"].Value?.ToString();
                row.LogName = value ?? string.Empty;
            }
        };

        // 「…」はセルの中のボタンなので、行選択ではなく内容のクリックとして拾う
        _grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
            if (_grid.Columns[e.ColumnIndex].Name != "logOpen") return;
            OpenLog(_rows[e.RowIndex]);
        };

        _fade.CheckedChanged += (_, _) =>
        {
            _opacity.Enabled = _fade.Checked;
            _opacityValue.Text = OpacityText();
            TransparencyPreview?.Invoke(_fade.Checked, _opacity.Value);
        };

        _opacity.ValueChanged += (_, _) =>
        {
            _opacityValue.Text = OpacityText();
            TransparencyPreview?.Invoke(_fade.Checked, _opacity.Value);
        };

        _ok.Click += (_, _) => Commit();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 大きさと位置を先に決める。ここで初めて、出したモニタの DPI が分かる
        ApplyInitialBounds();
        LayoutControls();
    }

    /// <summary>編集中の行をグリッドへ流し込む。選択位置は呼び出し側が決める。</summary>
    private void Reload(int selectIndex)
    {
        _grid.Rows.Clear();
        foreach (Row row in _rows)
        {
            _grid.Rows.Add(row.Number, row.DisplayName ?? string.Empty, row.Tab.Name, row.Visible,
                row.LogName);
        }

        if (_grid.Rows.Count == 0) return;
        int index = Math.Clamp(selectIndex, 0, _grid.Rows.Count - 1);
        _grid.ClearSelection();
        _grid.Rows[index].Selected = true;
        _grid.CurrentCell = _grid.Rows[index].Cells["displayName"];
    }

    /// <summary>
    /// 選択行を上下に動かす。**番号ごと入れ替える**ので、動かした行が相手の番号を受け取る。
    /// 番号を振り直さないのは、動かしていない行の番号を動かさないため。
    /// </summary>
    private void MoveRow(int delta)
    {
        int index = SelectedIndex();
        if (index < 0) return;

        int other = index + delta;
        if (other < 0 || other >= _rows.Count) return;

        (_rows[index].Number, _rows[other].Number) = (_rows[other].Number, _rows[index].Number);
        (_rows[index], _rows[other]) = (_rows[other], _rows[index]);

        Reload(other);
    }

    /// <summary>番号を 1 から詰め直す。並び順はそのまま。</summary>
    private void Compact()
    {
        int index = SelectedIndex();
        for (int i = 0; i < _rows.Count; i++) _rows[i].Number = i + 1;
        Reload(index < 0 ? 0 : index);
    }

    private void ClearName()
    {
        int index = SelectedIndex();
        if (index < 0) return;
        _rows[index].DisplayName = null;
        Reload(index);
    }

    /// <summary>スライダーの右に出す文字。切のときは値を出さない（効いていないため）。</summary>
    private string OpacityText() => _fade.Checked ? $"{_opacity.Value}%" : "—";

    private void Browse(TextBox target, string description)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
        };

        try
        {
            if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
        }
        catch
        {
            // 変な文字列が入っていても選択画面は開く
        }

        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
    }

    /// <summary>
    /// その行のログを既定のアプリで開く。
    ///
    /// まだ 1 度も送っていなければファイルは無い。ここで空ファイルを作ると「記録がある」と
    /// 誤解させるので作らず、無いことをそのまま伝える。
    ///
    /// 保存先は欄に書かれている値ではなく確定済みの設定を使う。欄を書き換えた直後は
    /// まだ保存されておらず、そちらを見ると存在しない場所を指すため。
    /// </summary>
    private void OpenLog(Row row)
    {
        string path = SendHistory.PathOf(SendHistory.Normalize(row.LogName));

        if (!File.Exists(path))
        {
            MessageBox.Show(this, "ログファイルがありません。", "CCIN",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show(this, "ログファイルを開けなかった。", "CCIN",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 欄に書かれているフォルダをエクスプローラーで開く。中に溜まったものを見るための導線。
    ///
    /// まだ 1 度も使っていないフォルダは存在しない。エラーを出して終わるのは不親切なので、
    /// 作ってから開く。作れなかったときだけ知らせる。
    /// </summary>
    private void OpenFolder(TextBox target)
    {
        string path = target.Text.Trim();
        if (path.Length == 0) return;

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show(this, "そのフォルダを開けなかった。パスを確かめて。",
                "CCIN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private int SelectedIndex() =>
        _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index : _grid.CurrentRow?.Index ?? -1;

    /// <summary>
    /// OK。編集中のセルを確定してから結果を作る。
    /// 名前を打った直後に OK を押すと、確定前の値が捨てられるため。
    /// </summary>
    private void Commit()
    {
        _grid.EndEdit();

        Result = _rows.ConvertAll(row =>
            new TargetEdit(row.Tab, row.Number, row.DisplayName, !row.Visible, row.LogName));
        ImageFolder = _folder.Text;
        LogFolder = _logFolder.Text;
        FadeWhenInactive = _fade.Checked;
        InactiveOpacity = _opacity.Value;
        AlwaysOnTop = _onTop.Checked;
        PreviewImage = _previewImage.Checked;
    }
}
