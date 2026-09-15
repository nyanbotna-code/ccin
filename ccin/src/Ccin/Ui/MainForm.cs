using Ccin.Core;

namespace Ccin.Ui;

/// <summary>
/// メイン画面。仮想デスクトップ 1 枚につき 1 インスタンス。
///
/// 画面層はここに閉じ込める。ウィンドウ列挙・タブ走査・送信は Core 側にあり、
/// Phase 4 で WPF へ載せ替えても Core はそのまま使える。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly VirtualDesktopService _desktops;
    private readonly TabScanner _scanner;
    private readonly Sender _sender;
    private readonly TargetNumbering _numbering;

    private readonly TextBox _body = new();
    private readonly ComboBox _targets = new();
    private readonly ThemedCheckBox _toActive = new();

    /// <summary>
    /// 直前に一覧へ出した表示内容の署名（番号・同一性・名前）。null は「まだ一度も作っていない」。
    /// これが変わったときだけ一覧を作り直す。
    /// </summary>
    private string? _listSignature;

    /// <summary>
    /// 直前の送信先の顔ぶれの署名（番号・同一性のみ。名前を含まない）。
    ///
    /// 名前と分けているのは、Claude Code のタブ名が状態に応じて刻々変わるため。
    /// 名前が変わるたびに知らせを出すと、送信結果や前面化の知らせが数秒で消えて読めない。
    /// 出入りがあったときだけ知らせる。
    /// </summary>
    private string? _targetSignature;
    private readonly Button _refresh = new();
    private readonly Button _activate = new();
    private readonly Button _image = new();
    private readonly Button _edit = new();
    private readonly Button _send = new();
    private readonly Label _hint = new();
    private readonly Label _statusLabel = new();
    private readonly Panel _bottom = new();

    private bool _busy;

    /// <summary>
    /// ユーザーが選んだ送信先。ドロップダウンの選択とは別に持つ。
    ///
    /// 一覧は走査のたびに作り直すため、走査が一時的に空を返すと選択が失われる。
    /// そのとき先頭を選び直すと、ユーザーが気付かないまま送信先が別のタブへ
    /// 移る。実際にそれで誤爆した。選択の記憶を一覧から独立させる。
    /// </summary>
    private TerminalTab? _chosen;

    /// <summary>直近に決めた To Active の送信先。表示と送信可否の判定に使う。</summary>
    private TerminalTab? _activeTarget;

    /// <summary>直前に知らせた To Active の送信先の署名。変わったときだけ知らせる。</summary>
    private string? _activeSignature;

    /// <summary>一覧を作り直している間は true。プログラムによる選択変更を拾わないため。</summary>
    private bool _updatingList;

    /// <summary>画面構成の変化が落ち着くのを待つ。連続して届く通知の最後の 1 回だけを効かせる。</summary>
    private readonly System.Windows.Forms.Timer _displaySettle = new() { Interval = 1500 };

    /// <summary>送信先の増減を拾うための定期走査。要求どおり 2000ms 間隔。</summary>
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };

    /// <summary>
    /// 処理が終わってから、操作を受け付け直すまでの冷却。詳しくは <see cref="BeginBusy"/> を見ること。
    ///
    /// 間隔は OS のダブルクリック判定時間をそのまま使う。固定値にしないのは
    /// 「OS がダブルクリックと呼ぶ速さの 2 回目」を押し間違いと言い切れるため。
    /// ユーザーのマウス設定にも自動で追従する。
    /// </summary>
    private readonly System.Windows.Forms.Timer _cooldown =
        new() { Interval = SystemInformation.DoubleClickTime };

    /// <summary>下段パネルの高さ。DPI に応じて実寸へ換算する。</summary>
    private const int BottomPanelLogicalHeight = 76;

    /// <summary>ヒントとステータスの 1 行分の高さ。</summary>
    private const int TextLineLogicalHeight = 16;

    /// <summary>レイアウト計算中。高さ変更で飛ぶ Resize による再入を防ぐ。</summary>
    private bool _layingOut;

    /// <summary>タイトルバーの高さの目安。掴めるかの判定に使う。</summary>
    private const int TitleBarLogicalHeight = 32;

    /// <summary>掴めるとみなすタイトルバーの重なり幅。マウスで確実に掴める幅として決めた値。</summary>
    private const int MinGrabLogicalWidth = 60;

    /// <summary>画面構成が変わったときに届く。</summary>
    private const int WM_DISPLAYCHANGE = 0x007E;

    /// <summary>この窓が受け持つ仮想デスクトップ。窓を出した時点で確定する。</summary>
    public Guid DesktopId { get; private set; } = Guid.Empty;

    public MainForm(VirtualDesktopService desktops, TabScanner scanner, Sender sender,
                    TargetNumbering numbering)
    {
        _desktops = desktops;
        _scanner = scanner;
        _sender = sender;
        _numbering = numbering;

        BuildUi();
        WireEvents();

        // 位置決めはハンドルが作られる前に済ませる。
        // 表示後に動かすと、主モニタで作ってから別倍率のモニタへ移すことになり、
        // その途中の DPI 切り替えで指定した大きさが失われる（保存サイズが効かなくなる）
        RestorePlacement();
    }

    // ---- 画面の組み立て -----------------------------------------------------

    private void BuildUi()
    {
        Text = AppInfo.WindowTitle;
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        Size = LogicalToDeviceUnits(new Size(560, 320));
        KeyPreview = true;

        // 要求の中核。仮想デスクトップをまたいだ表示は OS が面倒を見るので、
        // TopMost にしても自分のデスクトップにしか出てこない。
        // 切り替えられるようにしたので、設定から起こす（既定は入）
        TopMost = AppSettings.AlwaysOnTop;

        // 設定は窓ごとではなく CCIN 全体のもの。窓は机ごとに複数あるため、
        // 片方で変えた設定をもう片方が知らないと見え方が食い違う
        WindowSettingsChanged += ApplyWindowSettings;

        _body.Multiline = true;
        _body.ScrollBars = ScrollBars.Vertical;
        _body.AcceptsReturn = true;      // Enter は改行。送信は Ctrl+Enter
        _body.AcceptsTab = false;        // Tab はフォーカス移動に使う
        _body.WordWrap = true;
        _body.Dock = DockStyle.Fill;
        _body.Font = new Font(Font.FontFamily, 11f);

        _bottom.Dock = DockStyle.Bottom;

        _targets.Name = "targetCombo";
        _targets.DropDownStyle = ComboBoxStyle.DropDownList;
        _targets.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // 開いた一覧の選択行は、暗くしても OS の青のまま残る。自前で描いて配色に揃える
        _targets.DrawItem += (_, e) => Theme.DrawComboItem(_targets, e);

        // 送信先を選ばずに「今アクティブな端末」へ送るモード
        _toActive.Name = "toActiveCheck";
        _toActive.Text = "To Active";
        _toActive.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _toActive.TextAlign = ContentAlignment.MiddleLeft;

        _refresh.Name = "refreshButton";   // UI Automation から押せるようにする
        _refresh.Text = "リスト更新";
        _refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        // 同じ名前のタブが並ぶと、どれを選んでいるのか画面から判断できない。
        // 送らずに対象だけ前面へ出して、目で確かめるための逃げ道
        _activate.Name = "activateButton";
        _activate.Text = "最前面化";
        _activate.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        // クリップボードの画像を保存してパスを本文に入れる。
        // 送信先の選択とは無関係なので、常に押せる
        _image.Name = "imageButton";
        _image.Text = "画像";
        _image.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        // 表示名・番号・除外の編集。送信先の選択とは無関係なので常に押せる
        _edit.Name = "editButton";
        _edit.Text = "Edit";
        _edit.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _send.Name = "sendButton";
        _send.Text = "送信";
        _send.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _send.Font = new Font(Font.FontFamily, 10f, FontStyle.Bold);

        // 窓が狭いと末尾から省略される（AutoEllipsis）。使用頻度の高い順に並べる
        _hint.Text = "Ctrl+Enter:送信 / Ctrl+数字:選択 / F5:更新 / Ctrl+T:最前面";
        _hint.AutoSize = false;
        _hint.AutoEllipsis = true;
        _hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // ステータスはヒントの 2 行目として置く。ステータスバーを 1 本まるごと使うより
        // 下段が縦に詰まり、その分だけ本文欄が広がる
        _statusLabel.Name = "statusText";
        _statusLabel.Text = "準備中";
        _statusLabel.AutoSize = false;
        _statusLabel.AutoEllipsis = true;   // 長い行は末尾を省略する（折り返して下段を崩さない）
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _bottom.Controls.AddRange(
            [_targets, _toActive, _refresh, _activate, _hint, _statusLabel, _image, _edit, _send]);

        // Dock は追加の逆順に領域を取る。Fill を先に足すこと
        Controls.Add(_body);
        Controls.Add(_bottom);

        ApplyTheme();
    }

    /// <summary>
    /// 配色を当てる。**位置と大きさには一切触らない**（それは <see cref="LayoutBottomPanel"/> の担当）。
    /// 色の決定は <see cref="Theme"/> に寄せてあり、ここは当てる先を並べるだけ。
    /// </summary>
    private void ApplyTheme()
    {
        Theme.Window(this);

        Theme.Input(_body);
        Theme.Surface_(_bottom);

        Theme.Combo(_targets);
        Theme.Check(_toActive, Theme.Base);

        Theme.Button(_refresh);
        Theme.Button(_activate);
        Theme.Button(_image);
        Theme.Button(_edit);
        Theme.Primary(_send);   // 主たる操作。ここだけ差し色

        Theme.Text_(_hint, Theme.Base, muted: true);
        Theme.Text_(_statusLabel, Theme.Base);
    }

    /// <summary>
    /// 下段の高さ・部品の大きさ・座標を、**その時点の DPI でまとめて**決める。
    ///
    /// 以前は高さと大きさをコンストラクタで一度だけ決めていた。コンストラクタの時点では
    /// まだどのモニタに出るか分からず、150% のモニタに出ると位置だけが新しい DPI で
    /// 計算し直され、高さが古いままで送信ボタンが下段からはみ出した。
    /// 片方だけ古い DPI に取り残されないよう、ここに集約する。
    /// </summary>
    private void LayoutBottomPanel()
    {
        if (_layingOut) return;   // 高さを変えると Resize が飛ぶ。入れ子を防ぐ
        _layingOut = true;
        try
        {
            // 最小サイズもここで決める。コンストラクタで固定すると、150% のモニタでは
            // 論理値の 2/3 の大きさまで縮められてしまい、下段の部品が重なる
            MinimumSize = LogicalToDeviceUnits(new Size(420, 240));

            _bottom.Height = LogicalToDeviceUnits(BottomPanelLogicalHeight);
            _toActive.Size = LogicalToDeviceUnits(new Size(84, 26));
            _refresh.Size = LogicalToDeviceUnits(new Size(84, 26));
            _activate.Size = LogicalToDeviceUnits(new Size(84, 26));
            _image.Size = LogicalToDeviceUnits(new Size(64, 26));
            _edit.Size = LogicalToDeviceUnits(new Size(64, 26));
            _send.Size = LogicalToDeviceUnits(new Size(100, 26));

            int width = _bottom.ClientSize.Width;
            int pad = LogicalToDeviceUnits(8);
            int gap = LogicalToDeviceUnits(6);
            int row1 = LogicalToDeviceUnits(6);

            // 右端から 最前面化・リスト更新・To Active と並べ、残りをドロップダウンに与える
            _activate.Location = new Point(width - pad - _activate.Width, row1);
            _refresh.Location = new Point(_activate.Left - gap - _refresh.Width, row1);
            _toActive.Location = new Point(_refresh.Left - gap - _toActive.Width, row1);

            // 下限を設けると、狭いときにボタンの下へ潜り込んで重なる。
            // 読めない幅になってでも重ねないほうがよい
            int comboWidth = Math.Max(0, _toActive.Left - gap - pad);
            _targets.Location = new Point(pad, row1);
            _targets.Width = comboWidth;

            // 2 行のテキスト（ヒント / ステータス）と、その右に送信ボタン
            int lineHeight = LogicalToDeviceUnits(TextLineLogicalHeight);
            int textTop = LogicalToDeviceUnits(36);
            int textBottom = textTop + lineHeight * 2 + LogicalToDeviceUnits(2);

            // 要求どおり [画像][Edit][送信] の並び。右端が送信
            int buttonTop = textTop + ((textBottom - textTop - _send.Height) / 2);
            _send.Location = new Point(width - pad - _send.Width, buttonTop);
            _edit.Location = new Point(_send.Left - gap - _edit.Width, buttonTop);
            _image.Location = new Point(_edit.Left - gap - _image.Width, buttonTop);

            int textWidth = Math.Max(0, _image.Left - gap - pad);
            _hint.Location = new Point(pad, textTop);
            _hint.Size = new Size(textWidth, lineHeight);
            _statusLabel.Location = new Point(pad, textTop + lineHeight + LogicalToDeviceUnits(2));
            _statusLabel.Size = new Size(textWidth, lineHeight);
        }
        finally
        {
            _layingOut = false;
        }
    }

    private void WireEvents()
    {
        _refresh.Click += (_, _) => RefreshTargets();
        _activate.Click += (_, _) => ActivateSelected();
        _image.Click += (_, _) => AttachClipboardImage();
        _edit.Click += (_, _) => OpenEditDialog();

        _toActive.CheckedChanged += (_, _) =>
        {
            if (_toActive.Checked)
            {
                _activeTarget = ResolveActiveTarget();
                _activeSignature = ActiveSignature();
                ShowActiveTarget();
                SetStatus(DescribeActiveTarget());
            }
            else
            {
                // 外した瞬間に送信先を動かさない。今表示していたものをそのまま
                // ユーザーの選択として引き継ぐ。チェック前の選択には戻さない
                // （戻すと、外しただけで向き先が黙って変わる）
                _chosen = _activeTarget;
                _activeTarget = null;
                _activeSignature = null;
                ShowChosen();
                SetStatus($"送信先 {_targets.Items.Count} 件");
            }

            UpdateActionButtons();
        };
        _send.Click += (_, _) => Send();

        // 一覧の作り直しによる選択変更は無視し、ユーザーが選んだときだけ覚える
        _targets.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingList) return;
            _chosen = SelectedTab;
            UpdateActionButtons();
        };
        _bottom.Resize += (_, _) => LayoutBottomPanel();

        _body.KeyDown += (_, e) =>
        {
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.Return)
            {
                e.SuppressKeyPress = true;   // 本文に改行を残さない
                e.Handled = true;
                Send();
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                RefreshTargets();
                return;
            }

            // Ctrl+T で最前面を切り替える。Ctrl+Space にしないのは、IME の切り替えに
            // 割り当てている環境があるため。日本語を打つツールで踏みたくない競合
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.T)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;   // 本文に文字を残さない
                ToggleAlwaysOnTop();
                return;
            }

            // Ctrl+数字 で送信先を選び、対象を前面へ出す（送信はしない）。
            // 数字単独を発火キーにしないのは、本文に打った数字が混ざるため。
            // KeyPreview が立っているので本文欄に入力中でも先にここへ来る
            if (e.Control && !e.Shift && !e.Alt)
            {
                int number = DigitOf(e.KeyCode);
                if (number > 0)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;   // 本文に数字を残さない
                    SelectByNumber(number);
                }
            }
        };

        // モニタ間を移動して倍率が変わったら、下段を新しい DPI で組み直す
        DpiChanged += (_, _) => LayoutBottomPanel();

        _poll.Tick += (_, _) => PollTick();

        _cooldown.Tick += (_, _) =>
        {
            _cooldown.Stop();
            UpdateActionButtons();
        };

        _displaySettle.Tick += (_, _) =>
        {
            _displaySettle.Stop();
            EnsureGrabbable();

            string? warning = DpiWarning();
            if (warning is not null) SetStatus(warning);
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 新しい窓は「今いる仮想デスクトップ」に出る。表示されて初めて所属が決まるので、
        // ここで確定させる（コンストラクタでは取れない）
        DesktopId = _desktops.GetDesktopId(Handle) ?? Guid.Empty;

        // 出た瞬間の状態に合わせる。
        //
        // 起動時に他のアプリへフォーカスがあると、この窓は一度もアクティブにならず、
        // OnActivated も OnDeactivate も呼ばれない。濃さを触る機会が無いまま不透明で
        // 居座るため、ここで今の状態に合わせておく（実測で見つけた抜け）
        ApplyWindowSettings();

        LayoutBottomPanel();
        RefreshTargets();
        _poll.Start();
        _body.Focus();
    }

    /// <summary>
    /// 2000ms ごとの走査。送信先の増減と名前の変化を拾う。
    ///
    /// 走らせない場面が 3 つある。送信中（対象が入れ替わると事故になる）、
    /// ドロップダウンを開いている間（選んでいる最中に作り直すと選べない）、
    /// 自分が別のデスクトップにいる間（見えていない窓のために UI Automation を回す意味がない）。
    /// </summary>
    private void PollTick()
    {
        if (_busy || !IsHandleCreated) return;
        if (_targets.DroppedDown) return;
        if (!_desktops.IsOnCurrentDesktop(Handle)) return;

        RefreshTargets(announce: false);
    }

    /// <summary>
    /// 前回の位置と大きさに戻す。使えない位置ならプライマリ中央に出す。
    /// **中央に出した場合は保存しない**（ユーザーが選んだ位置ではないため）。
    /// </summary>
    private void RestorePlacement()
    {
        StartPosition = FormStartPosition.Manual;

        WindowBounds? saved = WindowPlacement.Load();
        if (saved is not null && saved.Width > 0 && saved.Height > 0)
        {
            var bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
            if (IsGrabbable(bounds))
            {
                Bounds = bounds;   // MinimumSize より小さい指定は WinForms が丸める
                return;
            }

            // 大きさはユーザーの選択なので活かす。位置だけ中央に戻す
            Bounds = PrimaryCenterBounds(new Size(saved.Width, saved.Height));
            return;
        }

        Bounds = PrimaryCenterBounds(Size);
    }

    /// <summary>
    /// タイトルバーを掴める位置にあるか。
    ///
    /// 窓全体ではなくタイトルバーを見るのは、**ユーザーが自力で救出できる境目がそこ**だから。
    /// Windows で窓をマウスで動かす手段はタイトルバーのドラッグしかない。本文が見えていても
    /// タイトルバーが画面の外にあれば、その窓はもう動かせない。
    ///
    /// 縦は「全部見えていること」を要求する。横だけ見ていたときは、下端に 8px だけ掛かった
    /// 窓が「掴める」と判定されて素通りした。ユーザーがドラッグで作れる位置は Windows 側が
    /// タイトルバーを画面内に留めるので、この条件で正当な位置を弾くことはない。
    /// </summary>
    private bool IsGrabbable(Rectangle bounds)
    {
        int titleHeight = LogicalToDeviceUnits(TitleBarLogicalHeight);
        int minGrab = LogicalToDeviceUnits(MinGrabLogicalWidth);
        var titleBar = new Rectangle(bounds.X, bounds.Y, bounds.Width, titleHeight);

        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle overlap = Rectangle.Intersect(screen.WorkingArea, titleBar);
            if (overlap.Height >= titleHeight && overlap.Width >= minGrab) return true;
        }
        return false;
    }

    /// <summary>
    /// プライマリの作業領域の中央。作業領域より大きい辺は作業領域に合わせる
    /// （そうしないと中央に置いたときタイトルバーが画面の上へはみ出す）。
    /// </summary>
    private static Rectangle PrimaryCenterBounds(Size preferred)
    {
        Screen screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        Rectangle work = screen.WorkingArea;

        int width = Math.Min(preferred.Width, work.Width);
        int height = Math.Min(preferred.Height, work.Height);

        return new Rectangle(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width,
            height);
    }

    /// <summary>
    /// 掴めない位置にいたらプライマリ中央へ移す。**保存はしない**。
    /// 画面構成が変わったときと、ccin.exe が再実行されたときに呼ばれる。
    /// 掴める状態なら何もしないので、何回呼んでも結果は同じ。
    /// </summary>
    public void EnsureGrabbable()
    {
        if (!IsHandleCreated) return;

        if (WindowState == FormWindowState.Minimized)
        {
            // 最小化中の Bounds は (-32000,-32000) になる。判定は復元後の位置で行う
            if (IsGrabbable(RestoreBounds)) return;
            WindowState = FormWindowState.Normal;
        }

        Rectangle current = Bounds;
        if (current.Width <= 0 || current.Height <= 0) return;
        if (IsGrabbable(current)) return;

        Bounds = PrimaryCenterBounds(current.Size);
    }

    /// <summary>
    /// ウィンドウメッセージの横取りは 1 つ。
    ///
    /// **WM_DISPLAYCHANGE**: 画面構成が変わった。解像度変更・モニタの抜き差し・配置変更で届く。
    /// すぐには動かさない。モニタの抜き差しでは配置と DPI の切り替えが連続して起きており、
    /// その最中にこちらが窓を動かすと Windows 側の処理と競合する。落ち着いてから判定する。
    /// タイマーは毎回はじめから測り直すので、連続して届いても最後の 1 回だけが効く。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == WM_DISPLAYCHANGE && IsHandleCreated)
        {
            _displaySettle.Stop();
            _displaySettle.Start();
        }
    }

    /// <summary>
    /// 窓の DPI とモニタの DPI が食い違っていないか。
    ///
    /// 食い違うと Windows が窓全体をその比率で拡大縮小して描画するため、部品がはみ出す。
    /// モニタの抜き差しの後にこの状態で固まることがあり、リサイズでは直らない。
    /// 直す手段が無いので、せめて何が起きているかを画面に出す。
    /// </summary>
    private string? DpiWarning()
    {
        if (!IsHandleCreated) return null;

        uint windowDpi = NativeMethods.GetDpiForWindow(Handle);
        uint monitorDpi = NativeMethods.GetMonitorDpi(Handle);
        if (windowDpi == 0 || monitorDpi == 0 || windowDpi == monitorDpi) return null;

        return $"表示倍率がモニタと不一致（窓 {windowDpi} / モニタ {monitorDpi}）。CCIN を起動し直すと直る。";
    }

    private void SavePlacement()
    {
        // 最大化中の Bounds は画面いっぱいの値になる。戻したときの大きさを覚える
        Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        WindowPlacement.Save(new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    /// <summary>
    /// ユーザーが移動・サイズ変更を終えた時点で覚える。
    ///
    /// **保存はここだけ**。閉じるときには保存しない。自動でプライマリ中央へ置いた位置まで
    /// 覚えてしまい、「ユーザーが置いた位置」が上書きされるため。
    /// このイベントはユーザー操作でしか飛ばないので、手動と自動の区別に判定もフラグも要らない。
    /// </summary>
    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SavePlacement();
    }

    // ---- 窓の見え方 ---------------------------------------------------------

    /// <summary>
    /// 窓の見え方の設定が変わった。**全ての窓**が自分に反映するために購読する。
    ///
    /// 設定は CCIN 全体のものだが、窓は仮想デスクトップごとに複数ある。
    /// 片方の窓で変えた値をもう片方が知らないと、同じ設定なのに見え方が食い違う。
    /// </summary>
    private static event Action? WindowSettingsChanged;

    /// <summary>設定を変えた側が呼ぶ。保存は呼び出し側の責任。</summary>
    private static void NotifyWindowSettingsChanged() => WindowSettingsChanged?.Invoke();

    /// <summary>
    /// 透過率の下見中だけ使う値。Edit ダイアログでスライダーを動かしている間、
    /// 保存前の値で見え方を確かめるために入る。閉じたら null に戻す。
    /// </summary>
    private (bool Fade, int Opacity)? _preview;

    /// <summary>今この窓が非アクティブのときに使う不透明度。下見中は下見の値を優先する。</summary>
    private double InactiveOpacityValue
    {
        get
        {
            (bool fade, int percent) = _preview ?? (AppSettings.FadeWhenInactive, AppSettings.InactiveOpacity);
            return fade ? percent / 100.0 : 1.0;
        }
    }

    /// <summary>設定を窓に反映する。前面の扱いと、今の状態に応じた濃さ。</summary>
    private void ApplyWindowSettings()
    {
        if (IsDisposed) return;

        TopMost = AppSettings.AlwaysOnTop;
        Opacity = ContainsFocus ? 1.0 : InactiveOpacityValue;
    }

    /// <summary>下見の値を入れる。Edit ダイアログのスライダーから呼ばれる。</summary>
    private void PreviewWindowSettings(bool fade, int percent)
    {
        _preview = (fade, percent);
        if (!ContainsFocus) Opacity = InactiveOpacityValue;
    }

    /// <summary>下見をやめる。OK でもキャンセルでも通る。</summary>
    private void ClearPreview()
    {
        _preview = null;
        ApplyWindowSettings();
    }

    /// <summary>
    /// 最前面を切り替える。設定を保存し、他の窓にも同じ状態を配る。
    ///
    /// 切ったことを忘れると「CCIN が出てこない」と誤解するため、状態を知らせに出す。
    /// </summary>
    private void ToggleAlwaysOnTop()
    {
        AppSettings.AlwaysOnTop = !AppSettings.AlwaysOnTop;
        AppSettings.Save();
        NotifyWindowSettingsChanged();
        SetStatus(AppSettings.AlwaysOnTop ? "最前面: 入" : "最前面: 切");
    }

    /// <summary>窓から離れたら薄くする。設定が切なら何も起きない。</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (IsDisposed) return;
        Opacity = InactiveOpacityValue;
    }

    /// <summary>
    /// 窓に戻ってきたときに一覧を取り直す。Phase 2 のポーリングが入るまでの繋ぎ。
    /// 送信中は対象が入れ替わると事故になるので触らない。
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // 触れる窓は必ず濃い。薄いまま操作させない
        if (!IsDisposed) Opacity = 1.0;

        if (_busy || !IsHandleCreated || DesktopId == Guid.Empty) return;

        // 窓を触っただけ。ユーザーが一覧を求めたわけではないので、知らせは変化があったときだけ
        RefreshTargets(announce: false);
    }

    // ---- 送信先の一覧 -------------------------------------------------------

    /// <summary>
    /// 送信先の一覧を取り直す。
    ///
    /// <paramref name="announce"/> が false のとき、件数などの知らせは
    /// **送信先の顔ぶれが実際に変わったときだけ** 出す。ポーリングから呼ばれる経路がこれで、
    /// 送信結果や前面化の知らせを上書きしないための線引きになっている。
    /// タブ名が変わっただけなら一覧は作り直すが、黙って作り直す。
    /// 走査そのものの失敗は、変化の有無にかかわらず必ず出す。
    /// </summary>
    private void RefreshTargets(bool announce = true)
    {
        if (_busy) return;

        if (!_desktops.IsAvailable)
        {
            SetStatus("仮想デスクトップ API が使えない。全ウィンドウが対象になる。");
        }

        // 窓をドラッグで別デスクトップへ移されている場合があるので毎回取り直す
        Guid? current = _desktops.GetDesktopId(Handle);
        if (current is not null) DesktopId = current.Value;

        if (DesktopId == Guid.Empty)
        {
            SetStatus("自分の所属デスクトップを特定できない。一覧を作れない。");
            _activeTarget = null;
            _ = ApplyTargets([], out _);
            UpdateActionButtons();
            return;
        }

        List<TerminalTab>? allTabs = ScanTerminalTabs(out string? scanError);
        if (allTabs is null)
        {
            SetStatus($"タブ走査に失敗した: {scanError}");
            return;
        }

        bool hadChoice = _chosen is not null;

        // 送信先を先に決めてから一覧へ渡す。ApplyTargets は To Active のとき
        // ここで決めた送信先を選択に映す
        _activeTarget = ResolveActiveAllowed(allTabs);
        string activeSignature = ActiveSignature();
        bool activeChanged = activeSignature != _activeSignature;
        _activeSignature = activeSignature;

        _ = ApplyTargets(allTabs, out bool targetSetChanged);

        // 一覧の中身が同じでも送信先だけ変わることがある（タブを切り替えたとき）。
        // ApplyTargets は何もせずに戻るので、表示合わせと有効・無効はここで必ず通す
        ShowActiveTarget();
        UpdateActionButtons();

        // To Active では、送信先が変わったこと自体が知らせる価値のある変化
        bool worthSaying = targetSetChanged || (_toActive.Checked && activeChanged);
        if (!announce && !worthSaying) return;

        if (_toActive.Checked)
        {
            SetStatus(DpiWarning() ?? DescribeActiveTarget());
        }
        else if (allTabs.Count == 0)
        {
            SetStatus("このデスクトップに端末が見つからない。");
        }
        else if (hadChoice && _targets.SelectedIndex < 0)
        {
            SetStatus($"選んでいた送信先が見つからない。選び直して。（送信先 {allTabs.Count} 件）");
        }
        else
        {
            SetStatus(DpiWarning() ?? $"送信先 {allTabs.Count} 件");
        }
    }

    /// <summary>
    /// 自分の机の端末タブを Z オーダー順（手前から）で返す。失敗したら null。
    ///
    /// 走査結果はそのまま送信先の一覧になる。並び順が Z オーダーであることは
    /// 「最前面」の判定に効くので、ここで崩さない。
    /// </summary>
    private List<TerminalTab>? ScanTerminalTabs(out string? error)
    {
        error = null;
        try
        {
            return _scanner.Scan(DesktopId, (uint)Environment.ProcessId);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// To Active の送信先を決める。**最前面のターミナルが今表示しているタブ**。
    ///
    /// 走査結果は Z オーダー順（ウィンドウは手前から、ウィンドウの中はタブ順）で並ぶ。
    /// 先頭のタブが属するウィンドウが最前面のターミナル。そのウィンドウが表示している
    /// タブがそのまま送信先になる。
    ///
    /// **表示していないタブへは決して送らない。** そこで別のタブへ回すと、ユーザーが
    /// 見ているのとは違う相手へ黙って送ることになる。実際にそれで誤送信が起きた。
    /// 送信先を全端末へ広げてもこの原則は変えない。
    ///
    /// 以前は「表示中のタブが Claude Code と認識できなければ送信先なし」としていた。
    /// 認識できない Claude Code（起動直後・信頼確認で停止中・タブ名を手動で固定したもの）が
    /// 実在し、そこへ回すと誤送信になるためだった。端末タブ全てを送信先にした今は、
    /// 認識の有無で止める理由がない。表示しているものへ送る、で足りる。
    ///
    /// 送信ボタンを押した瞬間の最前面は CCIN 自身なので、GetForegroundWindow は使わない。
    /// ターミナルが最前面のときはそのウィンドウが Z オーダーの先頭に来るため、同じ結果になる。
    /// </summary>
    private static TerminalTab? ResolveActive(List<TerminalTab> allTabs)
    {
        if (allTabs.Count == 0) return null;

        IntPtr front = allTabs[0].WindowHandle;

        foreach (TerminalTab tab in allTabs)
        {
            if (tab.WindowHandle != front || !tab.IsSelected) continue;
            return tab;
        }

        return null;
    }

    /// <summary>
    /// <see cref="ResolveActive"/> に除外の判定を足したもの。
    ///
    /// Edit で畳んだ送信先へは To Active でも送らない。一覧から消えているのに
    /// 最前面というだけで送信先になるのでは、畳んだ意味がない。
    /// 弾いた結果は「送信先なし」であって、別のタブへ回すことはしない。
    /// </summary>
    private TerminalTab? ResolveActiveAllowed(List<TerminalTab> tabs)
    {
        TerminalTab? tab = ResolveActive(tabs);
        if (tab is null) return null;
        return _numbering.IsExcluded(DesktopId, tab) ? null : tab;
    }

    /// <summary>
    /// 走査し直して To Active の送信先を決める。ポーリングの結果は最大 2 秒古く、
    /// その間に切り替えられていると別の送信先へ送ってしまう。
    /// </summary>
    private TerminalTab? ResolveActiveTarget()
    {
        if (DesktopId == Guid.Empty) return null;

        List<TerminalTab>? tabs = ScanTerminalTabs(out _);
        return tabs is null ? null : ResolveActiveAllowed(tabs);
    }

    /// <summary>
    /// 送信・前面化の直前に呼ぶ。走査し直して送信先を決め、一覧と灰色の表示もそこへ合わせる。
    /// 押した瞬間の状態がそのまま画面に出るので、どこへ送ったかを後から突き合わせられる。
    /// </summary>
    private TerminalTab? ResolveActiveTargetNow()
    {
        if (DesktopId == Guid.Empty) return null;

        List<TerminalTab>? tabs = ScanTerminalTabs(out _);
        if (tabs is null) return null;

        _activeTarget = ResolveActiveAllowed(tabs);
        _ = ApplyTargets(tabs, out _);
        ShowActiveTarget();
        UpdateActionButtons();
        return _activeTarget;
    }

    /// <summary>一覧に並んでいる送信先。表示合わせで使う。</summary>
    private List<TerminalTab> ListedTabs()
    {
        var listed = new List<TerminalTab>();
        foreach (object entry in _targets.Items)
        {
            if (entry is TargetItem item) listed.Add(item.Tab);
        }
        return listed;
    }

    /// <summary>
    /// To Active の送信先を灰色のドロップダウンに映す。
    ///
    /// 一覧の中身が変わっていないときは <see cref="ApplyTargets"/> が何もせずに戻るので、
    /// 選択の付け替えはここで独立して行う。**ユーザーの選択（_chosen）には触らない。**
    /// </summary>
    private void ShowActiveTarget()
    {
        if (!_toActive.Checked) return;

        List<TerminalTab> listed = ListedTabs();
        int index = -1;
        if (_activeTarget is not null)
        {
            TerminalTab? same = TerminalTab.FindSame(listed, _activeTarget);
            if (same is not null) index = listed.IndexOf(same);
        }

        if (_targets.SelectedIndex == index) return;

        _updatingList = true;
        try { _targets.SelectedIndex = index; }
        finally { _updatingList = false; }
    }

    /// <summary>ユーザーの送信先を選択に映す。To Active を外したときに使う。</summary>
    private void ShowChosen()
    {
        List<TerminalTab> listed = ListedTabs();
        int index = -1;
        if (_chosen is not null)
        {
            TerminalTab? same = TerminalTab.FindSame(listed, _chosen);
            if (same is not null) index = listed.IndexOf(same);
        }

        _updatingList = true;
        try { _targets.SelectedIndex = index; }
        finally { _updatingList = false; }
    }

    /// <summary>To Active の今の状態を 1 行で言う。</summary>
    private string DescribeActiveTarget()
    {
        if (_targets.Items.Count == 0) return "このデスクトップに端末が見つからない。";
        if (_activeTarget is null) return "最前面のターミナルの表示中タブが送信先にならない（除外か、特定できない）。";
        return $"To Active → {_numbering.LabelOf(DesktopId, _activeTarget)}";
    }

    /// <summary>送信先が変わったかを見るための署名。名前は入れない（状態で刻々変わるため）。</summary>
    private string ActiveSignature() =>
        _activeTarget is null ? string.Empty : $"{_activeTarget.RuntimeId}{_activeTarget.WindowHandle}";

    /// <summary>
    /// 一覧を作り直し、ユーザーが選んでいた送信先を選び直す。中身が前回と同じなら何もしない。
    ///
    /// **見つからないときに別の送信先を選ぶことはしない。** 選択を外し、送信を止め、
    /// ユーザーに選び直させる。黙って別の送信先へ向き先が変わるほうが危ない。
    /// </summary>
    /// <param name="targetSetChanged">
    /// 送信先の顔ぶれ（番号と同一性）が変わったなら true。名前だけの変化では true にしない。
    /// </param>
    /// <returns>一覧を作り直したなら true。表示内容が同じで何もしなかったなら false。</returns>
    private bool ApplyTargets(List<TerminalTab> tabs, out bool targetSetChanged)
    {
        // 番号は走査順ではなく TargetNumbering が決める。
        // 走査順はウィンドウの Z オーダーで、送信のたびに入れ替わるため一覧の基準にできない
        List<NumberedTab> assigned = _numbering.Assign(DesktopId, tabs);

        // Edit で畳んだものは一覧に出さない。番号は保持されるので、
        // 表示に戻したときに元の番号で戻ってくる
        List<NumberedTab> numbered = assigned.FindAll(n => !n.Excluded);
        var currentTabs = numbered.ConvertAll(n => n.Tab);

        // 顔ぶれ = 番号と同一性（RuntimeId・hwnd）。名前は入れない。
        // 表示 = それに名前を足したもの。どれかを落とすと「変わったのに一覧が古いまま」になり、
        // そのまま誤送信につながるので、同一性に効くものは全部入れる。
        // タブの選択状態は入れない。ターミナル側でタブを切り替えるたびに変わるうえ、
        // 一覧の表示内容とは関係がない
        string targets = string.Join("\u0002", numbered.ConvertAll(n =>
            $"{n.Number}\u0001{n.Tab.RuntimeId}\u0001{n.Tab.WindowHandle}"));
        string display = targets + "\u0003" + string.Join("\u0002", numbered.ConvertAll(n => n.Label));

        targetSetChanged = targets != _targetSignature;
        _targetSignature = targets;

        if (display == _listSignature) return false;
        _listSignature = display;

        _updatingList = true;
        _targets.BeginUpdate();
        try
        {
            _targets.Items.Clear();
            foreach (NumberedTab entry in numbered)
            {
                _targets.Items.Add(new TargetItem(entry.Number, entry.Tab, entry.Label));
            }

            int index = -1;
            if (_toActive.Checked)
            {
                // To Active のときは自動で決まった送信先を映すだけ。
                // ユーザーの選択（_chosen）はここでは作らないし、触らない
                if (_activeTarget is not null)
                {
                    TerminalTab? same = TerminalTab.FindSame(currentTabs, _activeTarget);
                    if (same is not null)
                    {
                        index = currentTabs.IndexOf(same);
                        _activeTarget = same;    // 会話名の変化に追従させる
                    }
                }
            }
            else if (_chosen is not null)
            {
                TerminalTab? same = TerminalTab.FindSame(currentTabs, _chosen);
                if (same is not null)
                {
                    index = currentTabs.IndexOf(same);
                    _chosen = same;          // 会話名の変化に追従させる
                }
            }
            else if (_targets.Items.Count == 1)
            {
                // まだ選んでいない状態で候補が 1 つだけなら、それを選ぶ。
                // 2 つ以上あるときに勝手に決めると、どれに送るか分からないまま送信できてしまう
                index = 0;
                _chosen = currentTabs[0];
            }

            _targets.SelectedIndex = index;
        }
        finally
        {
            _targets.EndUpdate();
            _updatingList = false;
        }

        UpdateActionButtons();
        return true;
    }

    // ---- Edit ダイアログと Ctrl+数字 -----------------------------------------

    /// <summary>
    /// 表示名・番号・除外を編集する画面を開く。
    ///
    /// 開いている間はポーリングを止める。走査が走ると番号の割り当てが動き、
    /// ダイアログが持っている写しと食い違う。編集中に一覧が作り替わるのも避けたい。
    /// </summary>
    private void OpenEditDialog()
    {
        if (!Accepting) return;

        _poll.Stop();
        try
        {
            List<NumberedTab> current = _numbering.Current(DesktopId);
            if (current.Count == 0)
            {
                SetStatus("編集できる送信先がない。リスト更新してから開いて。");
                return;
            }

            using var dialog = new EditDialog(current);

            // スライダーを動かしている間、保存前の値で見え方を確かめられるようにする。
            // このダイアログが前面にいる間、この窓はちょうど非アクティブになっている
            dialog.TransparencyPreview += PreviewWindowSettings;

            DialogResult answer;
            try
            {
                answer = dialog.ShowDialog(this);
            }
            finally
            {
                // キャンセルでも OK でも下見は必ず畳む。残すと設定と見え方が食い違う
                dialog.TransparencyPreview -= PreviewWindowSettings;
                ClearPreview();
            }

            if (answer != DialogResult.OK) return;

            _numbering.Apply(DesktopId, dialog.Result);

            bool imageChanged =
                !string.Equals(dialog.ImageFolder, AppSettings.ImageFolder, StringComparison.Ordinal);
            bool logChanged =
                !string.Equals(dialog.LogFolder, AppSettings.LogFolder, StringComparison.Ordinal);

            bool windowChanged =
                dialog.FadeWhenInactive != AppSettings.FadeWhenInactive ||
                dialog.InactiveOpacity != AppSettings.InactiveOpacity ||
                dialog.AlwaysOnTop != AppSettings.AlwaysOnTop;

            // 画像の扱いは窓の見え方ではないので、配る対象に入れない（設定を見るだけ）
            bool previewChanged = dialog.PreviewImage != AppSettings.PreviewImage;
            if (previewChanged) AppSettings.PreviewImage = dialog.PreviewImage;

            if (imageChanged) AppSettings.ImageFolder = dialog.ImageFolder;
            if (logChanged) AppSettings.LogFolder = dialog.LogFolder;

            if (windowChanged)
            {
                AppSettings.FadeWhenInactive = dialog.FadeWhenInactive;
                AppSettings.InactiveOpacity = dialog.InactiveOpacity;
                AppSettings.AlwaysOnTop = dialog.AlwaysOnTop;
            }

            if (imageChanged || logChanged || windowChanged || previewChanged) AppSettings.Save();

            // 窓の見え方は机ごとの窓すべてに配る。ここで配らないと、もう片方の窓だけ
            // 古い見え方のまま残る
            if (windowChanged) NotifyWindowSettingsChanged();

            // 署名を捨てて必ず作り直させる。番号・名前・除外のどれが変わっても
            // 一覧に反映されないと、画面と送信先が食い違う
            _listSignature = null;
            _targetSignature = null;
            RefreshTargets();
        }
        finally
        {
            _poll.Start();
        }
    }

    /// <summary>`Ctrl` と一緒に押された数字。数字でなければ 0。テンキーも受ける。</summary>
    private static int DigitOf(Keys key) => key switch
    {
        >= Keys.D1 and <= Keys.D9 => key - Keys.D0,
        >= Keys.NumPad1 and <= Keys.NumPad9 => key - Keys.NumPad0,
        _ => 0,
    };

    /// <summary>
    /// 番号で送信先を選び、対象を前面へ出す。**送信はしない。**
    ///
    /// 前面化まで行うのは、同じ名前のタブが並ぶ環境で「今どれを選んだか」を
    /// 目で確かめられるようにするため。定義書の 2 ストローク（選ぶ → 送る）の前半にあたる。
    /// </summary>
    private void SelectByNumber(int number)
    {
        if (!Accepting) return;

        if (_toActive.Checked)
        {
            SetStatus("To Active 中は番号で選べない。チェックを外して。");
            return;
        }

        TerminalTab? tab = _numbering.ByNumber(DesktopId, number);
        if (tab is null)
        {
            SetStatus($"{number} 番の送信先がない。");
            return;
        }

        List<TerminalTab> listed = ListedTabs();
        TerminalTab? same = TerminalTab.FindSame(listed, tab);
        if (same is null)
        {
            SetStatus($"{number} 番が一覧にない。リスト更新して。");
            return;
        }

        _chosen = same;
        _updatingList = true;
        try { _targets.SelectedIndex = listed.IndexOf(same); }
        finally { _updatingList = false; }

        UpdateActionButtons();
        ActivateSelected();
    }

    /// <summary>
    /// 操作を受け付けてよい状態か。処理中でも冷却中でもないときだけ true。
    ///
    /// 冷却中かどうかはタイマー自身が持っているので、フラグは増やさない。
    /// </summary>
    private bool Accepting => !_busy && !_cooldown.Enabled;

    /// <summary>
    /// 操作の受付状態を画面へ反映する。送信と前面化は「送信先が選ばれていること」も前提。
    ///
    /// 受け付けない間は、押されたくない部品をまとめて無効にする。無効な部品は
    /// <c>Click</c> を出さないので、遅れて配送されたクリックはここで落ちる。
    /// </summary>
    private void UpdateActionButtons()
    {
        // To Active のときは送信先が決まっているかどうかで見る。
        // 候補が居ても「最前面の窓でどれを見ていたか分からない」ときは決まっていない
        bool hasTarget = _toActive.Checked
            ? _activeTarget is not null
            : _targets.SelectedIndex >= 0;

        // 押されると困る 3 つは、冷却が明けるまで戻さない
        bool accepting = Accepting;
        _send.Enabled = accepting && hasTarget;
        _activate.Enabled = accepting && hasTarget;

        // 画像と Edit は送信先の選択を必要としないので hasTarget を見ない
        _image.Enabled = accepting;
        _edit.Enabled = accepting;

        // ここから下は処理が終われば戻してよい。冷却が止めたいのは「もう一度押される」ことだけで、
        // 打ち直しや送信先の選び直しまで待たせる理由はない。
        //
        // 処理中に無効にするのは、送信中に書き換えられると送った内容と手元の内容が
        // 食い違うため。成功時に本文を空にするので、その間に打っていた文字も失われる
        _body.Enabled = !_busy;
        _refresh.Enabled = !_busy;
        _toActive.Enabled = !_busy;

        // To Active のときは送信先を自動で決めるので、ドロップダウンは触らせない
        _targets.Enabled = !_busy && !_toActive.Checked;
    }

    /// <summary>
    /// 他プロセスの窓を触っている間は true。<see cref="EndBusy"/> と必ず try/finally で対にする。
    ///
    /// うっかりダブルクリックすると、2 回目のクリックが送信を壊していた。本文は送信先に
    /// 貼り付けられたのに Enter が送られず、プロンプトに残ったままになる。さらに、
    /// 1 回目が失敗したときは本文が手元に残るので、2 回目がそのまま再送になりうる。
    ///
    /// 効かない対処を 3 つ実測した。
    ///
    /// - **ボタンだけ無効**: クリックはボタンに届かなくても窓には届き、窓が活性化される
    /// - **窓ごと無効（Enabled = false）**: 無効な窓は活性化されない代わりに、
    ///   その瞬間に前面が**どの窓でもない状態**になる（`GetForegroundWindow()` が 0）
    /// - **`WM_MOUSEACTIVATE` を断る**: 活性化は止まるが、止まるのはマウスの経路だけ。
    ///   Alt+Tab やタスクバーからの活性化は素通りする。しかも断っている間は
    ///   窓のどこもクリックできない（本文欄もタイトルバーも）
    ///
    /// 前面を奪われること自体は <see cref="Core.Sender"/> の復帰が経路を問わず処理する
    /// （奪った相手が自分か「どの窓でもない」ときだけ取り戻し、他アプリなら中止する）。
    /// ここで防ぐのは 2 回目の <c>Click</c> が起きることだけでよい。
    ///
    /// 解除は <see cref="EndBusy"/> が冷却へ渡す。処理が終わった瞬間には受け付け直さない。
    /// </summary>
    private void BeginBusy()
    {
        _busy = true;
        _cooldown.Stop();
        UpdateActionButtons();
    }

    /// <summary>
    /// 処理を終える。ただしここでは受け付け直さず、冷却へ渡す。
    ///
    /// 処理中に投げられたクリックは、処理が終わってから配送されることがある
    /// （画像保存のように途中でメッセージを回さない経路では必ずそうなる）。
    /// 冷却の間は部品が無効なままなので、そのクリックは <c>Click</c> を出さずに落ちる。
    /// </summary>
    private void EndBusy()
    {
        _busy = false;
        _cooldown.Stop();
        _cooldown.Start();
        UpdateActionButtons();
    }


    /// <summary>
    /// 選んでいる送信先を送らずに前面へ出す。
    ///
    /// 会話名が付く前の Claude Code も、素のシェルも、一覧では同じ名前で並ぶ。
    /// 送る前に「これで合っているか」を目で確かめる手段が要る。
    /// </summary>
    private void ActivateSelected()
    {
        if (!Accepting) return;

        // 押した瞬間から塞ぐ。2 回目のクリックが CCIN を前面へ戻すと、
        // せっかく前面化した対象がまた隠れて目的を果たせない
        BeginBusy();
        try
        {
            TerminalTab? tab = CurrentTarget;
            if (tab is null)
            {
                SetStatus(_toActive.Checked
                    ? DescribeActiveTarget()
                    : "送信先が未選択。リストから選んで。");
                return;
            }

            if (!tab.IsAlive)
            {
                SetStatus("送信先が閉じているか入れ替わっている。リスト更新して選び直して。");
                return;
            }

            if (!TabScanner.SelectTab(tab))
            {
                SetStatus("前面化できなかった。対象を一度クリックしてから再試行して。");
                return;
            }

            // うっかりの 2 回目でこちらが前面へ戻ってしまうと、前面化した意味が無い。
            // 少し粘って、自分のせいで奪われていたら取り戻す
            Sender.HoldForeground(tab);
            SetStatus($"前面化: {tab.Name}  [hwnd 0x{tab.WindowHandle.ToInt64():X}]");
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>
    /// クリップボードの画像を保存し、本文の末尾にフルパスを足して、既定のアプリで開く。
    ///
    /// 開くのは中身を目で確かめるため。プレビューを自前で描くと画面の切り替えや
    /// 状態管理が要るが、エクスプローラーと同じ既定のビューアに任せれば要らない。
    /// </summary>
    private void AttachClipboardImage()
    {
        if (!Accepting) return;

        BeginBusy();
        try
        {
            ImageSaveResult result = ImageClip.SaveFromClipboard();

            if (result.Outcome != ImageSaveOutcome.Saved || result.Path is null)
            {
                SetStatus(result.Message);
                return;
            }

            // 書きかけの本文は消さない。行頭にしてからパスを足す
            if (_body.TextLength > 0 && !_body.Text.EndsWith('\n'))
            {
                _body.AppendText(Environment.NewLine);
            }

            // パスは引用符で囲む。空白を含むパスをそのまま貼ると、受け手側で
            // 途中で切れて別の引数として読まれる。既に囲まれていれば足さない
            _body.AppendText(Quoted(result.Path) + Environment.NewLine);

            // 中身を見せるかは設定次第。切っているときに「開けなかった」と出すと、
            // 失敗したように読めてしまうので、そもそも試さず文言も足さない
            if (!AppSettings.PreviewImage)
            {
                SetStatus(result.Message);
                return;
            }

            SetStatus(ImageClip.TryOpen(result.Path)
                ? result.Message
                : result.Message + "（開けなかった）");
        }
        finally
        {
            EndBusy();
            _body.Focus();
        }
    }

    /// <summary>
    /// パスを引用符で囲む。すでに囲まれていればそのまま返す。
    ///
    /// 二重に囲むと受け手側で引用符そのものがパスの一部として読まれる。
    /// </summary>
    private static string Quoted(string path)
    {
        string trimmed = path.Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"')) return trimmed;
        return $"\"{trimmed}\"";
    }

    private TerminalTab? SelectedTab =>
        _targets.SelectedItem is TargetItem item ? item.Tab : null;

    // ---- 送信 ---------------------------------------------------------------

    /// <summary>
    /// 今の送信先。To Active のときは走査し直して決め、一覧と灰色の表示もそこへ合わせる。
    /// そうでなければ選ばれているもの。
    /// </summary>
    private TerminalTab? CurrentTarget => _toActive.Checked ? ResolveActiveTargetNow() : SelectedTab;

    private void Send()
    {
        if (!Accepting) return;

        // 押した瞬間から塞ぐ。送信先の決定には UI Automation の走査が入るので、
        // ここを後回しにするとその間の 2 回目のクリックが通ってしまう
        BeginBusy();
        try
        {
            TerminalTab? tab = CurrentTarget;
            if (tab is null)
            {
                SetStatus(_toActive.Checked
                    ? DescribeActiveTarget()
                    : "送信先が未選択。リストから選んで。");
                return;
            }

            string text = _body.Text;
            if (text.Length == 0)
            {
                SetStatus("本文が空。送信しない。");
                return;
            }

            SendResult result = _sender.Send(tab, text);
            SendLog.Write(tab, result, text.Length);

            // 本文を残すのはここだけ。診断用の --sendtest は通らないので、テストの本文が
            // 読み返し用の記録に混ざらない
            (int number, string? displayName, string logName) = _numbering.DescribeFor(DesktopId, tab);
            SendHistory.Write(tab, number, displayName, logName, result, text);

            if (result.Outcome == SendOutcome.Sent)
            {
                // 以前ここには「Clear() と違って Undo 履歴が残る」と書いてあったが、**誤り**。
                // 変更前のビルドで実測したところ、送信後の Ctrl+Z では本文は戻らなかった。
                // SelectedText の代入自体がやり直しバッファを空にするため。
                // 送信後に本文を取り戻す手段は今のところ無い（未対応。報告書の Pending 参照）
                _body.SelectAll();
                _body.SelectedText = string.Empty;
            }

            // CCIN が内部で何かしたときは、それを先に出す。ステータスは 1 行で末尾から
            // 切れるため、識別子より注記を前に置く（注記は読めないと意味がない）
            string note = string.IsNullOrEmpty(result.Note) ? string.Empty : $"  [{result.Note}]";

            // 表示だけでは対象の同一性を後から追えない。識別子まで残す
            SetStatus($"{result.Message}{note}  [hwnd 0x{tab.WindowHandle.ToInt64():X}]");
        }
        finally
        {
            EndBusy();
            _body.Focus();
        }
    }

    private void SetStatus(string message) => _statusLabel.Text = message;


    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _poll.Dispose();
            _displaySettle.Dispose();
            _cooldown.Stop();
            _cooldown.Dispose();

            // 静的イベントは窓より長生きする。外し忘れると閉じた窓が掴まれ続ける
            WindowSettingsChanged -= ApplyWindowSettings;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// ドロップダウンの 1 行。番号は <see cref="TargetNumbering"/> が決めた固定番号で、
    /// そのタブが消えるまで動かない。
    /// </summary>
    private sealed record TargetItem(int Number, TerminalTab Tab, string Label)
    {
        public override string ToString() => $"{Number}: {Label}";
    }
}
