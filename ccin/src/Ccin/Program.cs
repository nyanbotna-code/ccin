namespace Ccin;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 枠・タイトルバー・スクロールバー・一覧の下地・右クリックメニューを暗くする。
        // 窓を 1 枚でも作った後では効かないので、ここより後ろへ動かしてはいけない。
        // 表（DataGridView）と一覧の選択行だけはこれでも暗くならず、Theme が明示的に埋める
        Application.SetColorMode(
            Core.AppSettings.DarkMode ? SystemColorMode.Dark : SystemColorMode.Classic);

        // 開発時の診断。実機で API が動くかを実測してファイルに残す
        if (args.Length >= 2 && args[0] == "--probe")
        {
            Core.Probe.Run(args[1]);
            return;
        }

        // タブ名のサンプリング。Claude Code が回している記号の集合を実測する
        if (args.Length >= 3 && args[0] == "--titles" && int.TryParse(args[2], out int seconds))
        {
            Core.Probe.SampleTitles(args[1], seconds);
            return;
        }

        // 窓とモニタの DPI が食い違っていないかの実測
        if (args.Length >= 2 && args[0] == "--dpi")
        {
            Core.Probe.DpiReport(args[1]);
            return;
        }

        // send.log の hwnd / RuntimeId を実物のタブに突き合わせる
        if (args.Length >= 2 && args[0] == "--tabsall")
        {
            Core.Probe.ListAllTabs(args[1]);
            return;
        }

        // 自分の窓が仮想デスクトップごとに 1 枚になっているかの実測
        if (args.Length >= 2 && args[0] == "--windows")
        {
            Core.Probe.ListOwnWindows(args[1]);
            return;
        }

        // 送信経路の実測。画面を操作せずに Core.Sender をそのまま通す
        if (args.Length >= 4 && args[0] == "--sendtest")
        {
            Core.Probe.SendTest(args[1], args[2], args[3]);
            return;
        }

        using Core.SingleInstance instance = Core.SingleInstance.Acquire();

        // 2 個目以降は窓を出さない。常駐に「今いるデスクトップへ窓を出せ」と合図して消える
        if (!instance.IsPrimary)
        {
            instance.SignalPrimary();
            return;
        }

        using var context = new Ui.CcinApplicationContext(instance);
        Application.Run(context);
    }
}
