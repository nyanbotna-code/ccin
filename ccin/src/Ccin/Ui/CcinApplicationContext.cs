using Ccin.Core;

namespace Ccin.Ui;

/// <summary>
/// 常駐プロセスの本体。仮想デスクトップ 1 枚につき窓 1 枚を受け持つ。
///
/// プロセスを 1 個に集約する理由は、Phase 3 で入る表示名と番号を全窓で共有するため。
/// プロセスを分けると共有の仕組みを別途作ることになる。
/// </summary>
internal sealed class CcinApplicationContext : ApplicationContext
{
    private readonly VirtualDesktopService _desktops = new();
    private readonly TabScanner _scanner;
    private readonly Sender _sender = new();

    /// <summary>
    /// 送信先の番号。全ての窓で 1 つを共有する（デスクトップごとに番号空間は分かれる）。
    /// プロセスを 1 個に集約している理由がこれ。
    /// </summary>
    private readonly TargetNumbering _numbering = new();

    private readonly List<MainForm> _forms = [];

    /// <summary>
    /// 別プロセスからの合図をワーカースレッドで受けるため、UI スレッドへ渡す足場が要る。
    /// 親を持たない Control は Handle を触るまでウィンドウを作らない。
    /// </summary>
    private readonly Control _marshal = new();

    public CcinApplicationContext(SingleInstance instance)
    {
        _scanner = new TabScanner(_desktops);

        _ = _marshal.Handle;   // BeginInvoke するにはハンドルが要る

        instance.ActivationRequested += OnActivationRequested;
        instance.StartListening();

        EnsureWindowOnCurrentDesktop();
    }

    /// <summary>2 個目以降の ccin.exe が起動された。ワーカースレッドから呼ばれる。</summary>
    private void OnActivationRequested()
    {
        if (!_marshal.IsHandleCreated) return;
        try
        {
            _marshal.BeginInvoke(EnsureWindowOnCurrentDesktop);
        }
        catch (ObjectDisposedException)
        {
            // 終了処理と競合した。合図を落として構わない
        }
    }

    /// <summary>
    /// 今いる仮想デスクトップに窓が無ければ作り、あれば前面に出す。
    ///
    /// デスクトップの一覧を取る非公開 API は使わない。新しく作った窓は
    /// OS が「今いるデスクトップ」に置くので、それで足りる。
    /// </summary>
    private void EnsureWindowOnCurrentDesktop()
    {
        foreach (MainForm form in _forms)
        {
            if (!_desktops.IsOnCurrentDesktop(form.Handle)) continue;

            if (form.WindowState == FormWindowState.Minimized)
            {
                form.WindowState = FormWindowState.Normal;
            }

            // 見えないから起動し直した、という場合がある。前面化する前に救出しておく
            form.EnsureGrabbable();

            form.Show();
            NativeMethods.Activate(form.Handle);
            return;
        }

        var created = new MainForm(_desktops, _scanner, _sender, _numbering);
        created.FormClosed += OnFormClosed;
        _forms.Add(created);
        created.Show();
        NativeMethods.Activate(created.Handle);
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is not MainForm form) return;

        form.FormClosed -= OnFormClosed;
        _forms.Remove(form);

        // 最後の窓が閉じたらプロセスごと終わる。常駐だけが残ると
        // 次の起動が「合図するだけで窓が出ない」状態になり、見た目には起動しないアプリになる
        if (_forms.Count == 0) ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _marshal.Dispose();
        base.Dispose(disposing);
    }
}
