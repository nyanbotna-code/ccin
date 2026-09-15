using System.Windows.Automation;

namespace Ccin.Core;

internal enum SendOutcome
{
    Sent,
    TargetGone,
    ActivationFailed,
    ForegroundMismatch,
    ForegroundLostBeforeEnter,
    PasteNotConfirmed,
    ClipboardFailed,
    EmptyBody,
}

/// <summary>
/// 送信の結果。
///
/// <paramref name="Note"/> は「CCIN が内部で何をしたか」の短い要約で、ステータス欄と
/// 送信ログの両方が同じ文字列を使う。両者で同じ値を使うのは、「画面には出たが記録に
/// 残っていない」という食い違いを構造的に起こさないため。
///
/// ステータス欄は 1 行で、長いと末尾が切れて送信先名まで読めなくなる。だから注記は
/// 定型の短い語に限る。詳しい経緯は <see cref="SendTrace"/> が持つ。
/// </summary>
internal sealed record SendResult(SendOutcome Outcome, string Message, string? Note = null);

/// <summary>
/// 本文を対象タブへ送る。
///
/// クリップボード貼り付けを使うのは、日本語と複数行をそのまま端末へ通せる唯一の方法だから。
/// その代償として「狙ったウィンドウ以外に打ち込む」事故が起きうるため、
/// 各段でフォアグラウンドが対象かを検証し、一致しなければ Enter を送らずに中止する。
/// </summary>
internal sealed class Sender
{
    /// <summary>
    /// 貼り付けから Enter までの基準待ち時間。**画面を読めない端末でしか使わない。**
    ///
    /// 以前はこれが唯一の待ちだったが、時間で待つ方式は貼り付けの確認ダイアログが
    /// 出ている場合に破綻する（何 ms 待ってもダイアログは消えない）。
    /// 画面が読める端末では <see cref="ConfirmPaste"/> が到達そのものを確認する。
    /// </summary>
    private const int BasePasteDelayMs = 300;

    /// <summary>本文が長いほど貼り付け完了が遅れるので、文字数に応じて延ばす。</summary>
    private const int PasteDelayPerCharDivisor = 8;

    private const int MaxPasteDelayMs = 2000;

    /// <summary>
    /// 貼り付けが端末に届くのを待つ上限。
    ///
    /// 時間で待つのではなく到達を確認するので、ここは「諦める境目」でしかない。
    /// 実測では 5200 字でも 1 秒かからずに届く。10 秒はそれに対する余裕。
    /// </summary>
    private const int PasteConfirmTimeoutMs = 10000;

    /// <summary>到達を見に行く間隔。画面の読み取りは実測 0.2ms なので細かく見ても負荷にならない。</summary>
    private const int PasteConfirmPollMs = 100;

    /// <summary>Enter の後、画面が落ち着くまでの追加待ち。診断のためだけに待つ。</summary>
    private const int SettleDelayMs = 600;

    /// <summary>
    /// 前面を取り戻す試行の上限。連打 1 回につき 1 度奪われるので、
    /// うっかりの範囲（3 連打まで）を吸収できる数にしてある。
    /// これを超える連打は事故ではないとみなし、従来どおり中止する。
    /// </summary>
    private const int MaxForegroundRecoveries = 3;

    /// <summary>
    /// 送信の途中で拾った記録。診断用で、送信の成否には関与しない。
    /// </summary>
    private sealed class Trace
    {
        public string LiveTitle = string.Empty;
        public string SelectDetail = string.Empty;
        public string PasteDetail = string.Empty;
        public string FocusBeforeEnter = string.Empty;
        public string PopupsBeforeEnter = string.Empty;
        public string GuiThreadBeforeEnter = string.Empty;
        public string? BeforePaste;
        public string? AfterPaste;
        public string? AfterEnter;
        public string? AfterEnterSettled;
    }

    public SendResult Send(TerminalTab tab, string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new SendResult(SendOutcome.EmptyBody, "本文が空。送信しない。");
        }

        if (!tab.IsAlive)
        {
            return new SendResult(SendOutcome.TargetGone,
                "送信先が閉じているか別のウィンドウに入れ替わっている。リスト更新して選び直して。",
                "送信先が消失");
        }

        string? clipboardBackup = TryGetClipboardText();

        if (!TrySetClipboardText(body))
        {
            return new SendResult(SendOutcome.ClipboardFailed, "クリップボードに書けなかった。",
                "クリップボード書込失敗");
        }

        var trace = new Trace();
        try
        {
            SendResult result = SendCore(tab, body, trace);
            SendTrace.Write(tab, trace.LiveTitle, body,
                trace.SelectDetail, trace.PasteDetail, trace.FocusBeforeEnter,
                trace.PopupsBeforeEnter, trace.GuiThreadBeforeEnter,
                trace.BeforePaste, trace.AfterPaste, trace.AfterEnter,
                trace.AfterEnterSettled, result);
            return result;
        }
        finally
        {
            if (clipboardBackup is not null)
            {
                TrySetClipboardText(clipboardBackup);
            }
        }
    }

    /// <summary>
    /// 送信の本体。各段で記録を <paramref name="trace"/> に積む。
    /// 記録は診断専用で、読めなくても送信は続ける。
    /// </summary>
    private static SendResult SendCore(TerminalTab tab, string body, Trace trace)
    {
        if (!TabScanner.SelectTab(tab, out string selectDetail))
        {
            trace.SelectDetail = selectDetail;
            return new SendResult(SendOutcome.ActivationFailed,
                "送信先を前面に出せなかった。対象を一度クリックしてから再試行して。",
                "前面化できず");
        }
        trace.SelectDetail = selectDetail;

        Wait(250);

        if (!EnsureForeground(tab, trace))
        {
            return new SendResult(SendOutcome.ForegroundMismatch,
                "前面が送信先と一致しない。誤爆防止のため中止した。",
                "送信先不一致で中止");
        }

        // 前面化した直後なら、ウィンドウのタイトルは対象タブの今の名前になっている。
        // 一覧の表示名が古いままだったかどうかがここで判る
        trace.LiveTitle = NativeMethods.GetWindowTitle(tab.WindowHandle);

        TerminalTextReader screen = TerminalTextReader.Open(tab.WindowHandle);
        trace.BeforePaste = screen.ReadTail(SendTrace.TailChars);

        SendKeys.SendWait("^v");

        // 時間ではなく**到達**で判断する。実測で、5 KiB を超える本文では Windows Terminal が
        // 「強制的に貼り付け」の確認を出し、続けて打った Enter がそのボタンに吸われていた。
        // 本文は入力欄に残るのに outcome は Sent になる、という最悪の壊れ方をする。
        // ダイアログは時間では消えないので、待ちを延ばしても直らない
        if (!ConfirmPaste(tab, screen, body, trace, out string? pasteNote))
        {
            return new SendResult(SendOutcome.PasteNotConfirmed,
                "貼り付けが端末に届いたことを確認できなかった。Enter は送っていない。",
                "到達せず中止");
        }

        trace.AfterPaste = screen.ReadTail(SendTrace.TailChars);

        if (!EnsureForeground(tab, trace))
        {
            return new SendResult(SendOutcome.ForegroundLostBeforeEnter,
                "貼り付け後にフォーカスが外れた。Enter は送っていない。",
                "前面が外れて中止");
        }

        // Enter がどこへ行くのかを残す。タブ見出しにフォーカスが載っていれば端末に届かない
        trace.FocusBeforeEnter = TerminalTextReader.DescribeFocus();
        trace.PopupsBeforeEnter = NativeMethods.DescribePopups();
        trace.GuiThreadBeforeEnter = NativeMethods.DescribeGuiThread(tab.WindowHandle);

        // 到達を確認した後でも、ここで送信先のボタンにフォーカスが載っていたら打たない。
        // Enter がそのボタンを押して終わり、本文は入力欄に残る
        if (FocusedButtonOf(tab) is not null)
        {
            AppendPaste(trace, "button focused at enter");
            return new SendResult(SendOutcome.PasteNotConfirmed,
                "端末側の確認ボタンにフォーカスが載っている。誤操作防止のため Enter を送っていない。",
                "確認ボタン検出で中止");
        }

        SendKeys.SendWait("{ENTER}");
        Wait(150);

        trace.AfterEnter = screen.ReadTail(SendTrace.TailChars);

        // 150ms では再描画が間に合わないことがある。落ち着いてからもう 1 枚撮る
        Wait(SettleDelayMs);
        trace.AfterEnterSettled = screen.ReadTail(SendTrace.TailChars);

        return new SendResult(SendOutcome.Sent, $"送信 {DateTime.Now:HH:mm:ss} → {tab.Name}", pasteNote);
    }

    /// <summary>
    /// 貼り付けが端末に届いたことを確認する。届いたと言い切れなければ false。
    ///
    /// 判定は「画面が貼り付け前から変化したか」で行う。本文そのものを画面で照合しないのは、
    /// Claude Code が長い貼り付けを `[Pasted text #N]` に畳んで表示するため、本文の末尾が
    /// 画面に出てこないから。**受け手の表示の作り方に依存しない判定**にしてある。
    ///
    /// 途中で貼り付けの確認ダイアログが出ていたら確定させてから待ち続ける。CCIN 自身が
    /// 要求した貼り付けを CCIN が承認する形なので筋は通る。他アプリからの貼り付けに対する
    /// Windows Terminal の警告はそのまま残る。
    /// </summary>
    private static bool ConfirmPaste(TerminalTab tab, TerminalTextReader screen, string body, Trace trace,
        out string? note)
    {
        note = null;

        // 画面を読めない端末では到達を見届けられない。従来どおり時間で待つしかない
        if (!screen.IsAvailable)
        {
            int delay = Math.Min(MaxPasteDelayMs,
                BasePasteDelayMs + body.Length / PasteDelayPerCharDivisor);
            Wait(delay);
            trace.PasteDetail = $"screen unavailable; waited {delay}ms";
            note = "画面を読めず時間待ち";
            return true;
        }

        string? before = trace.BeforePaste;
        DateTime started = DateTime.Now;
        int dialogs = 0;

        while ((DateTime.Now - started).TotalMilliseconds < PasteConfirmTimeoutMs)
        {
            Wait(PasteConfirmPollMs);

            // ダイアログが出ている間は、画面が変わっていても本文は届いていない。先に片付ける
            string? button = ConfirmPasteDialog(tab);
            if (button is not null)
            {
                dialogs++;
                AppendPaste(trace, $"dialog '{button}' confirmed");
                continue;
            }

            string? now = screen.ReadTail(SendTrace.TailChars);
            if (now is not null && now != before)
            {
                int elapsed = (int)(DateTime.Now - started).TotalMilliseconds;
                AppendPaste(trace, $"arrived in {elapsed}ms (dialogs={dialogs})");

                // ダイアログを押していれば、利用者から見えないところで CCIN が操作している。
                // 黙って処理して成功だけ返すと、何が起きたか誰にも判らない
                if (dialogs > 0) note = "端末の確認ダイアログを自動確定";
                return true;
            }
        }

        AppendPaste(trace, $"NOT arrived in {PasteConfirmTimeoutMs}ms (dialogs={dialogs})");
        return false;
    }

    /// <summary>
    /// 送信先が出している確認ダイアログのボタンにフォーカスが載っていたら押す。
    /// 押したボタン名を返す。何もしなかったら null。
    /// </summary>
    private static string? ConfirmPasteDialog(TerminalTab tab)
    {
        AutomationElement? button = FocusedButtonOf(tab);
        if (button is null) return null;

        try
        {
            string name = button.Current.Name ?? string.Empty;
            if (button.GetCurrentPattern(InvokePattern.Pattern) is InvokePattern invoke)
            {
                invoke.Invoke();
                return name.Length > 0 ? name : "(unnamed)";
            }
        }
        catch
        {
            // 押せなかった。次の周回で見直す
        }
        return null;
    }

    /// <summary>
    /// 今フォーカスを持っている要素が**送信先プロセスのボタン**ならそれを返す。
    ///
    /// プロセスまで照合するのは、別のアプリの窓を押す事故を防ぐため。種別をボタンに
    /// 限るのも同じ理由で、IME の変換候補は ListItem として出るのでここに掛からない。
    /// </summary>
    private static AutomationElement? FocusedButtonOf(TerminalTab tab)
    {
        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused is null) return null;
            if (focused.Current.ControlType != ControlType.Button) return null;
            if (focused.Current.ProcessId != (int)tab.ProcessId) return null;
            return focused;
        }
        catch
        {
            // 走査中に消えた・UIA が応答しない。押さないのが安全側
            return null;
        }
    }

    /// <summary>
    /// 前面化したあと少し粘り、その間に**自分のせいで**前面を奪われたら取り戻す。
    ///
    /// 前面化ボタンは対象を前面に出すのが目的なので、うっかりの 2 回目が CCIN を
    /// 前面へ戻すと目的が台無しになる。送信と同じ考え方で、クリックを止める予防ではなく
    /// 復帰で処理する。待っている間もメッセージを回すので、溜まったクリックはここで配送される。
    /// </summary>
    public static bool HoldForeground(TerminalTab tab, int holdMs = 400)
    {
        Wait(holdMs);
        return EnsureForeground(tab, null);
    }

    /// <summary>
    /// 送信先が前面にあることを確かめる。**自分のせいで外れた場合に限り**取り戻す。
    ///
    /// CCIN の窓を送信中にクリックすると（うっかりダブルクリックが典型）、Windows は
    /// 送信先の活性化を解除し、CCIN が前面に立つ。クリックが無効な部品に落ちた場合は
    /// 前面が **0**（どの窓でもない）になることもある。どちらの状態で中止しても、
    /// 貼り付け済みの本文が送信先のプロンプトに残ったまま Enter が送られない。実測で確認した。
    ///
    /// 取り戻すのは「前面が 0」または「前面が CCIN 自身」のときだけ。**別のアプリが前面なら
    /// 取り戻さない。** ユーザーが意図して別の窓へ移ったとみなし、従来どおり中止する。
    /// ここを緩めると、離席中に別のウィンドウを叩き起こして送ることになる。
    /// </summary>
    private static bool EnsureForeground(TerminalTab tab, Trace? trace)
    {
        if (NativeMethods.GetForegroundWindow() == tab.WindowHandle) return true;

        int recovered = 0;

        // 1 回では足りない。取り戻す間の待ちも DoEvents を回すので、そこで次のクリックが
        // 配送されてまた奪われる。連打された回数だけ奪われるため、こちらも粘る。
        // 他のアプリが前面になった時点で即やめるので、安全側の性質は変わらない
        for (int attempt = 0; attempt < MaxForegroundRecoveries; attempt++)
        {
            if (!LostToOurselves()) break;

            NativeMethods.Activate(tab.WindowHandle);
            recovered++;
            Wait(120);

            if (NativeMethods.GetForegroundWindow() == tab.WindowHandle)
            {
                Note(trace, recovered);
                return true;
            }
        }

        if (recovered > 0) Note(trace, recovered);
        return false;
    }

    /// <summary>貼り付けの経緯を 1 行に積む。区切りは 2 件目から入れる。</summary>
    private static void AppendPaste(Trace trace, string note)
    {
        trace.PasteDetail += trace.PasteDetail.Length == 0 ? note : $"; {note}";
    }

    /// <summary>取り戻したことを selection 行に足す。引数の並びを増やさずに済む。</summary>
    private static void Note(Trace? trace, int recovered)
    {
        if (trace is null) return;
        trace.SelectDetail += $"; foreground recovered (lost to ourselves) x{recovered}";
    }

    /// <summary>前面が「どの窓でもない」か「CCIN 自身」か。どちらも自分が原因。</summary>
    private static bool LostToOurselves()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return true;
        return NativeMethods.GetProcessId(foreground) == (uint)Environment.ProcessId;
    }

    /// <summary>UI を固めずに待つ。待機中もフォーカスの変化を拾えるようにする。</summary>
    private static void Wait(int milliseconds)
    {
        DateTime end = DateTime.Now.AddMilliseconds(milliseconds);
        while (DateTime.Now < end)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// クリップボードは他プロセスにロックされることがあるので数回粘る。
    /// テキスト以外は復元できないため、退避もテキストに限る。
    /// </summary>
    private static bool TrySetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }

    private static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch
        {
            return null;
        }
    }
}
