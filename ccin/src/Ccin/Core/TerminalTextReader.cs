using System.Windows.Automation;

namespace Ccin.Core;

/// <summary>
/// 端末の表示内容を読む。
///
/// 「貼り付けたのに送信されず入力欄に残る」事象の切り分けに使う。送信ログには結果しか
/// 残らないため、Enter が届いていないのか、届いたが送信として扱われなかったのかが判らない。
/// 画面そのものを前後で記録すれば、そこが判る。
///
/// 実測で、全文の取得は 0.2 ms、要素の取得が 7.4 ms。1 回の送信で数回読んでも体感に出ない。
/// </summary>
internal sealed class TerminalTextReader
{
    /// <summary>表示内容として扱う最小の長さ。タブ名だけの要素を弾く。</summary>
    private const int MinimumBufferLength = 100;

    private readonly TextPattern? _text;

    private TerminalTextReader(TextPattern? text) => _text = text;

    public bool IsAvailable => _text is not null;

    /// <summary>
    /// ウィンドウの表示内容を読む口を開く。要素の探索はここで 1 回だけ行い、
    /// 以降の読み取りは使い回す。
    /// </summary>
    public static TerminalTextReader Open(IntPtr hWnd)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(hWnd);
            if (root is null) return new TerminalTextReader(null);

            var condition = new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Text);
            AutomationElementCollection candidates = root.FindAll(TreeScope.Descendants, condition);

            foreach (AutomationElement candidate in candidates)
            {
                try
                {
                    if (candidate.GetCurrentPattern(TextPattern.Pattern) is not TextPattern pattern) continue;
                    if (pattern.DocumentRange.GetText(-1).Length < MinimumBufferLength) continue;
                    return new TerminalTextReader(pattern);
                }
                catch
                {
                    // この要素は読めない。次を試す
                }
            }
        }
        catch
        {
            // 端末が閉じられた、UIA が応答しない等。診断なので黙って諦める
        }
        return new TerminalTextReader(null);
    }

    /// <summary>
    /// 今キーボードフォーカスを持っている要素の説明。
    ///
    /// 複数タブの窓では、タブを選ぶ操作でフォーカスがタブ見出し側に載る可能性がある。
    /// そうなると貼り付け（ウィンドウ側で処理される）は効くのに Enter が端末へ届かない。
    /// Enter を打つ直前に記録しておけば、次の失敗でそこが判る。
    /// </summary>
    public static string DescribeFocus()
    {
        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused is null) return "(none)";

            string type = focused.Current.ControlType?.ProgrammaticName ?? "?";
            string name = focused.Current.Name ?? string.Empty;
            string className = focused.Current.ClassName ?? string.Empty;
            return $"{type} class='{className}' name='{name}'";
        }
        catch (Exception ex)
        {
            return $"(failed: {ex.GetType().Name})";
        }
    }

    /// <summary>
    /// 表示内容の末尾を返す。入力欄は画面の末尾にあるので、そこだけあれば足りる。
    /// 読めなければ null。
    /// </summary>
    public string? ReadTail(int maxChars)
    {
        if (_text is null) return null;
        try
        {
            string all = _text.DocumentRange.GetText(-1).TrimEnd();
            return all.Length <= maxChars ? all : all[^maxChars..];
        }
        catch
        {
            return null;
        }
    }
}
